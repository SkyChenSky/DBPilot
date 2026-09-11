using Chloe;
using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Collecting;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DBPilot.Core.SlowSql;

/// <summary>
/// 慢SQL采集服务（SlowSqlJob 每 30s 调用）：
/// 遍历启用实例 → 幂等保障采集通道 → 增量读取 → 解析 + 文本归一化指纹 → 批量落库。
/// 双通道：SQL Server 走 XE 会话 + fn_xe（平台侧解析事件 XML）；MySQL 走 mysql.slow_log 表 start_time 水位（引擎侧解析）。
/// 并发读被监控实例、平台库写串行（Chloe 上下文非线程安全）；失败退避与采样底座共用 CollectStateStore；
/// 会话创建/读取失败写 dbpilot_instance.last_error 供页面标注"明细采集不可用"。
/// 注：clock_skew_seconds 探测时存绝对值（无方向）做不了时间校正，云/NTP 环境 skew≈0，事件时间直接用 XE timestamp（UTC）。
/// </summary>
public class SlowSqlCollectService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    ProviderRegistry registry, XeCursorStore cursors, CollectStateStore states, CollectOptions options, IPlatformDialect dialect) : IDepend
{
    private const int BatchSize = 500;

    /// <summary>采集入口：遍历启用实例并行增量读取慢SQL事件，串行解析落库并推进游标。</summary>
    public async Task CollectAllAsync(CancellationToken ct = default)
    {
        var entities = await CollectRunner.LoadEnabledAsync(sp);
        if (entities is null) return;
        var db = sp.GetRequiredService<DbContext>();

        var parsed = new ConcurrentBag<(int InstanceId, List<SlowSqlRecord> Records)>();
        // 采集状态标注（写 last_error 供页面显示"明细采集不可用"）：并行段只收集，串行段统一落库（Chloe 非线程安全）
        var flags = new ConcurrentDictionary<int, string?>();
        // 并行段前的旧游标快照（落库失败回退用，at-least-once）
        var oldCursors = entities.ToDictionary(e => e.Id, e => cursors.SlowSqlOf(e.Id));

        // 自动探测到 XE 目录的实例（串行段回写 xe_file_path，探测成功一拍后本门槛不再进入）
        var detected = new ConcurrentDictionary<int, string>();

        await CollectRunner.ForEachEnabledAsync(entities, states, options, "慢SQL采集", ct, async (e, token) =>
        {
            // 无慢SQL事件通道的引擎（能力矩阵 slowSqlEvents=none，如 PostgreSQL）：采集整段跳过——
            // 降级形态（Top SQL 模板榜）走 top_sql_delta 聚合查询，不跑采集也不写 last_error 标注
            if (registry.CapabilityOf(e.Engine, DbpilotCapabilityKeys.SlowSqlEvents) == DbpilotCapabilityLevel.None)
                return;

            var state = states.Of(e.Id, "慢SQL采集");

            // XE 门槛按引擎能力矩阵判定（SQL Server 走 XE 文件会话，MySQL 走 slow_log 表；
            // Supports 内含 engine 空/白 = sqlserver 兜底）。目录留空时自动探测（兑现界面"留空自动探测"承诺）：
            // ProbeAsync 的 SERVERPROPERTY('ErrorLogFileName') 剥文件名得默认日志目录，成功回填实体本拍即用
            // 并在串行段落库；探测不到（受限环境属性可 NULL）才标注"明细采集不可用"——非故障，不消耗退避
            if (registry.Supports(e.Engine, DbpilotFeatures.SlowSqlXeChannel) && string.IsNullOrWhiteSpace(e.XeFilePath))
            {
                try
                {
                    var meta = await provider.ProbeAsync(InstanceConfigResolver.ToConfig(crypto, e), token);
                    if (string.IsNullOrWhiteSpace(meta.DefaultLogPath))
                    {
                        flags[e.Id] = "慢SQL明细采集不可用：实例未配置 XE 文件目录（xe_file_path）且自动探测失败，请手动填写";
                        return;
                    }
                    e.XeFilePath = meta.DefaultLogPath;
                    detected[e.Id] = meta.DefaultLogPath;
                    Log.Information("实例 {Id} 自动探测 XE 文件目录：{Path}", e.Id, meta.DefaultLogPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    flags[e.Id] = "慢SQL明细采集不可用：实例未配置 XE 文件目录（xe_file_path）且自动探测失败，请手动填写";
                    Log.Debug(ex, "实例 {Id} XE 目录自动探测失败", e.Id);
                    return;
                }
            }

            Exception? failure = null;
            try
            {
                var cfg = InstanceConfigResolver.ToConfig(crypto, e);
                await provider.EnsureSlowSqlCaptureAsync(cfg, token);
                var read = await provider.PollSlowSqlAsync(cfg, cursors.SlowSqlOf(e.Id), token);
                state.OnSuccess();
                cursors.SetSlowSql(e.Id, read.NewCursor);   // 无论有无事件，游标推进到已读位置

                flags[e.Id] = null;   // 成功即清标注

                // 双通道：MySQL 引擎侧已解析（slow_log 表）直接用；SQL Server 走 XE XML 平台侧解析（duration 实测定论 µs）
                var records = read.Records ?? read.Events
                    .Select(x => SlowSqlEventParser.ParseEvent(x.EventData, durationIsMicroseconds: true))
                    .OfType<SlowSqlRecord>()
                    .ToList();
                if (records.Count > 0)
                    Log.Information("实例 {Id} 慢SQL增量读取 {Events} 条事件，解析有效 {Records} 条", e.Id, read.Records?.Count ?? read.Events.Count, records.Count);
                if (records.Count > 0) parsed.Add((e.Id, records));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // 标注随失败走（与骨架退避日志同源）；异常继续上抛给骨架记失败退避（取消除外）
                failure = ex;
                throw;
            }
            finally
            {
                if (failure != null)
                    flags[e.Id] = $"慢SQL明细采集不可用：{failure.GetDeepestException().Message.Sub(500)}";
            }
        });

        // 平台库写串行：状态标注 + 探测目录回填 + 落库；落库失败回退游标（下轮全量重扫该窗口，±3ms 指纹判重保证幂等）
        var persisted = await CollectRunner.PersistAsync("慢SQL落库失败", async () =>
        {
            foreach (var (id, error) in flags)
                db.SqlQuery<int>(
                    error is null ? dialect.InstanceLastErrorClearSql() : dialect.InstanceLastErrorSetSql(),
                    new { id, error }).FirstOrDefault();

            // 自动探测到的 XE 目录落库（局部更新只 SET 该列，成功后下拍起不再探测）
            foreach (var (id, path) in detected)
                await db.UpdateAsync<DbpilotInstance>(x => x.Id == id, x => new DbpilotInstance { XeFilePath = path });

            await PersistAsync(db, parsed.ToList());
        });
        if (!persisted)
            foreach (var id in parsed.Select(x => x.InstanceId).Distinct())
                cursors.SetSlowSql(id, oldCursors.GetValueOrDefault(id));
    }

    /// <summary>判重落库：键 (instance_id, event_time, fingerprint, duration_ms) —— 游标重扫/文件滚动重读不重复插入；
    /// 按本批事件时间范围查已存键集合过滤后 InsertRange，每批 500 条事务。
    /// 事件时间统一截断到毫秒（XE 时间戳亚毫秒精度 vs DATETIME2(3) 四舍五入，精确 == 会失配 —— 死锁采集同款结论）。</summary>
    private static async Task PersistAsync(DbContext db, List<(int InstanceId, List<SlowSqlRecord> Records)> parsed)
    {
        foreach (var (instanceId, records) in parsed)
        {
            var from = records.Min(r => r.EventTimeUtc).AddSeconds(-1);
            var to = records.Max(r => r.EventTimeUtc).AddSeconds(1);
            var existing = (await db.Query<DbpilotSlowSql>()
                    .Where(x => x.InstanceId == instanceId && x.EventTime >= from && x.EventTime <= to)
                    .ToListAsync())
                .Select(x => (x.EventTime.Ticks, Key: (x.Fingerprint, x.DurationMs)))
                .ToHashSet();

            // ±3ms 邻域匹配（口径见 XeEventDedup，与死锁采集共用）
            var fresh = records
                .Where(r => !XeEventDedup.Seen(existing, r.EventTimeUtc.TruncateToMillisecond().Ticks, (r.Fingerprint, r.DurationMs)))
                .Select(r => new DbpilotSlowSql
                {
                    InstanceId = instanceId,
                    EventTime = r.EventTimeUtc.TruncateToMillisecond(),
                    DbName = r.DbName,
                    LoginName = r.LoginName,
                    HostName = r.HostName,
                    AppName = r.AppName,
                    SessionId = r.SessionId,
                    SqlType = r.SqlType,
                    DurationMs = r.DurationMs,
                    CpuMs = r.CpuMs,
                    LogicalReads = r.LogicalReads,
                    PhysicalReads = r.PhysicalReads,
                    Writes = r.Writes,
                    RowCount = r.RowCount,
                    Fingerprint = r.Fingerprint,
                    SqlText = r.SqlText,
                    CreateTime = DateTime.UtcNow,
                })
                .ToList();
            if (fresh.Count == 0) continue;

            for (var i = 0; i < fresh.Count; i += BatchSize)
                await db.InsertRangeAsync(fresh.Skip(i).Take(BatchSize).ToList());
        }
    }
}
