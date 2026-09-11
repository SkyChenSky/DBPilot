using Chloe;
using DBPilot.Common;
using DBPilot.Core.Deadlocks;
using DBPilot.Core.Instances;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.Providers;
using DBPilot.Core.SlowSql;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.Collections.Concurrent;

namespace DBPilot.Core.Collecting;

/// <summary>XE 采集游标存储（实例 → 游标；进程内存态，重启后首次全量重扫 + 指纹判重兜底）。
/// 死锁与慢SQL 两组游标各自独立（不同 XE 会话/文件，互不影响；合并自原 DeadlockCursorStore/SlowSqlCursorStore）。</summary>
public class XeCursorStore : ISingletonDepend
{
    private readonly ConcurrentDictionary<int, DeadlockCursor?> _deadlock = new();
    private readonly ConcurrentDictionary<int, SlowSqlCursor?> _slowSql = new();

    public DeadlockCursor? DeadlockOf(int instanceId) => _deadlock.TryGetValue(instanceId, out var c) ? c : null;

    public void SetDeadlock(int instanceId, DeadlockCursor? cursor) => _deadlock[instanceId] = cursor;

    public SlowSqlCursor? SlowSqlOf(int instanceId) => _slowSql.TryGetValue(instanceId, out var c) ? c : null;

    public void SetSlowSql(int instanceId, SlowSqlCursor? cursor) => _slowSql[instanceId] = cursor;
}

/// <summary>
/// 并行采集骨架（五个采集 Job 共用：SessionSampling / TopSqlDelta / Deadlock / SlowSql / QueryPlan）：
/// 加载 Enabled 实例 → Parallel.ForEachAsync（Collect.MaxDegreeOfParallelism）→ CollectStateStore 退避跳过
/// → 异常 OnFailure + 连续失败 Warning / 单次失败 Debug 双级日志；检测结果集中后在并行段后串行落库
/// （Chloe 上下文非线程安全）。采集委托内自行 state.OnSuccess() 与收集结果；
/// 非故障跳过场景（如慢SQL 未配置 xe_file_path）在委托内直接 return、不触碰采集状态即可。
/// </summary>
public static class CollectRunner
{
    /// <summary>平台库可用时加载全部启用实例；平台库未配置或无实例返回 null（调用方直接收工）。</summary>
    public static async Task<List<DbpilotInstance>?> LoadEnabledAsync(IServiceProvider sp)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var entities = await db.Query<DbpilotInstance>().Where(x => x.Enabled).ToListAsync();
        return entities.Count == 0 ? null : entities;
    }

    /// <summary>并行遍历采集（label 进日志：如 "死锁采集"、"采样"）。委托抛出即记失败退避；取消异常正常上抛。</summary>
    public static Task ForEachEnabledAsync(IReadOnlyList<DbpilotInstance> entities, CollectStateStore states,
        CollectOptions options, string label, CancellationToken ct,
        Func<DbpilotInstance, CancellationToken, Task> collect)
        => Parallel.ForEachAsync(entities,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = ct,
            },
            async (e, token) =>
            {
                var state = states.Of(e.Id, label);
                if (state.InBackoff) return;

                try
                {
                    await collect(e, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (DbpilotUnsupportedException ex)
                {
                    // 引擎能力边界（未注册引擎 / 功能无对等数据源）：按实例静默跳过，不计退避不 WARN——
                    // 同引擎每拍都会走到这里，记 Debug 留痕即可（各 Provider 显式抛出）
                    Log.Debug(ex, "实例 {Instance}（{Id}）引擎 {Engine} 不支持{Label}，跳过", e.Name, e.Id, e.Engine, label);
                }
                catch (Exception ex)
                {
                    if (state.OnFailure(options.FailureThreshold, options.BackoffSeconds))
                        Log.Warning(ex, "实例 {Instance}（{Id}）连续{Label}失败 {Threshold} 次，退避 {Backoff}s",
                            e.Name, e.Id, label, options.FailureThreshold, options.BackoffSeconds);
                    else
                        Log.Debug(ex, "实例 {Instance}（{Id}）{Label}失败", e.Name, e.Id, label);
                }
            });

    /// <summary>串行落库包装：成功返回 true，异常只记 Error 不上抛（单实例落库失败不拖垮整个 Job；errorLabel 进日志）。
    /// 返回 false 时调用方可回退内存态（如 XE 游标）实现 at-least-once —— 下轮重扫 + ±3ms 指纹判重天然幂等。</summary>
    public static async Task<bool> PersistAsync(string errorLabel, Func<Task> persist)
    {
        try
        {
            await persist();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Label}", errorLabel);
            return false;
        }
    }
}

/// <summary>XE 事件时间判重口径（死锁/慢SQL 采集共用）：
/// XE 时间戳亚毫秒精度 vs 平台库 DATETIME2(3) 舍入，且 Chloe DateTime 参数按 SQL datetime 3.33ms 网格发送，
/// 非网格值（如 .035）入库进位到 .037（最大偏 1.67ms）→ 落库前 TruncateToMillisecond + 判重 ±3ms 邻域窗口，
/// 精确 == 必失配（SQL 侧范围查询与本侧集合匹配都须用此口径）。</summary>
internal static class XeEventDedup
{
    /// <summary>判重邻域窗口（±3ms）。</summary>
    internal const int WindowMs = 3;

    /// <summary>已存键集合（截断毫秒 ticks + 其余键段）中是否已含 ticks 的 ±3ms 邻域内任一时刻。</summary>
    internal static bool Seen<T>(HashSet<(long Ticks, T Key)> set, long ticks, T key)
    {
        for (var i = -WindowMs; i <= WindowMs; i++)
            if (set.Contains((ticks + i * TimeSpan.TicksPerMillisecond, key)))
                return true;
        return false;
    }
}
