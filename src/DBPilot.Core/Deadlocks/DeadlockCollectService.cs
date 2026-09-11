using Chloe;
using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Collecting;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.Deadlocks;

/// <summary>
/// 死锁采集服务（DeadlockJob 每 60s 调用）：
/// 遍历启用实例 → ReadDeadlockEventsAsync（版本双路径增量）→ 平台侧解析 → 指纹判重落库 dbpilot_deadlock_event。
/// 并发读被监控实例、平台库写串行（Chloe 上下文非线程安全）；失败退避与采样底座共用 CollectStateStore（按 实例+采集器 独立计数）。
/// </summary>
public class DeadlockCollectService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    XeCursorStore cursors, CollectStateStore states, CollectOptions options) : IDepend
{
    /// <summary>采集入口：遍历启用实例并行增量读取 XE 死锁事件并解析，串行指纹判重落库。</summary>
    public async Task CollectAllAsync(CancellationToken ct = default)
    {
        var entities = await CollectRunner.LoadEnabledAsync(sp);
        if (entities is null) return;
        var db = sp.GetRequiredService<DbContext>();

        var parsed = new ConcurrentBag<(int InstanceId, TimeSpan UtcOffset, List<DeadlockEventModel> Events)>();
        // 并行段前的旧游标快照（落库失败回退用，at-least-once）
        var oldCursors = entities.ToDictionary(e => e.Id, e => cursors.DeadlockOf(e.Id));

        await CollectRunner.ForEachEnabledAsync(entities, states, options, "死锁采集", ct, async (e, token) =>
        {
            var state = states.Of(e.Id, "死锁采集");
            var cfg = InstanceConfigResolver.ToConfig(crypto, e);
            var read = await provider.ReadDeadlockEventsAsync(cfg, cursors.DeadlockOf(e.Id), token);
            state.OnSuccess();
            cursors.SetDeadlock(e.Id, read.NewCursor);   // 无论有无事件，游标推进到已读位置

            var models = read.Events
                .Select(x => DeadlockReportParser.ParseEvent(x.EventData, read.LocalUtcOffset))
                .OfType<DeadlockEventModel>()
                .ToList();
            if (models.Count > 0) parsed.Add((e.Id, read.LocalUtcOffset, models));
        });

        // 平台库写串行；落库失败回退游标（下轮全量重扫该窗口，±3ms 指纹判重保证幂等）
        var persisted = await CollectRunner.PersistAsync("死锁事件落库失败", () => PersistAsync(db, parsed.ToList()));
        if (!persisted)
            foreach (var id in parsed.Select(x => x.InstanceId).Distinct())
                cursors.SetDeadlock(id, oldCursors.GetValueOrDefault(id));
    }

    /// <summary>指纹判重落库：判重键 (instance_id, fingerprint, event_time) —— 游标重扫不重复插入；
    /// 相似死锁再次发生（同指纹不同时刻）正常保留（趋势统计口径）。
    /// 事件 + 进程/资源维度行同事务写入（维度缺行会污染统计）。</summary>
    private static async Task PersistAsync(
        DbContext db, List<(int InstanceId, TimeSpan UtcOffset, List<DeadlockEventModel> Events)> parsed)
    {
        db.Session.BeginTransaction();
        try
        {
            foreach (var (instanceId, offset, models) in parsed)
            {
                foreach (var m in models)
                {
                    // 平台库 event_time 为 DATETIME2(3)，截断到毫秒 + ±3ms 邻域判重（口径见 XeEventDedup）。
                    // 窗口边界须在 C# 侧先算好：AddMilliseconds 进表达式树会被 Chloe.MySql 翻译成
                    // 非法的 DATE_ADD(x, MILLISECOND)（缺 INTERVAL），MySQL 平台库形态落库必炸
                    var eventTime = m.EventTimeUtc.TruncateToMillisecond();
                    var w = XeEventDedup.WindowMs;
                    var from = eventTime.AddMilliseconds(-w);
                    var to = eventTime.AddMilliseconds(w);
                    var exists = await db.Query<DbpilotDeadlockEvent>().AnyAsync(x =>
                        x.InstanceId == instanceId && x.Fingerprint == m.Fingerprint
                        && x.EventTime >= from && x.EventTime <= to);
                    if (exists) continue;

                    // Chloe Insert 返回实体本身，自增 id 插入后回填到属性上
                    var evt = new DbpilotDeadlockEvent
                    {
                        InstanceId = instanceId,
                        EventTime = eventTime,
                        VictimSpids = string.Join(",", m.Processes.Where(p => p.IsVictim).Select(p => p.Spid)),
                        Fingerprint = m.Fingerprint,
                        DeadlockGraph = m.GraphXml,
                        UtcOffsetMinutes = (int)Math.Round(offset.TotalMinutes),
                        CreateTime = DateTime.UtcNow,
                    };
                    await db.InsertAsync(evt);
                    var eventId = evt.Id;

                    foreach (var p in m.Processes)
                        await db.InsertAsync(new DbpilotDeadlockProcess
                        {
                            EventId = eventId,
                            InstanceId = instanceId,
                            EventTime = eventTime,
                            Spid = p.Spid,
                            IsVictim = p.IsVictim,
                            LoginName = p.LoginName,
                            HostName = p.HostName,
                            ClientApp = p.ClientApp,
                            IsolationLevel = p.IsolationLevel,
                            LockMode = p.LockMode,
                            WaitResource = p.WaitResource,
                            Status = p.Status,
                            TransactionName = p.TransactionName,
                            LogUsed = p.LogUsed,
                            WaitTimeMs = p.WaitTimeMs,
                            Trancount = p.Trancount,
                            LastTranStarted = p.LastTranStartedUtc,
                            InputBuf = p.InputBuf,
                            CreateTime = DateTime.UtcNow,
                        });

                    foreach (var r in m.Resources)
                        await db.InsertAsync(new DbpilotDeadlockResource
                        {
                            EventId = eventId,
                            InstanceId = instanceId,
                            EventTime = eventTime,
                            ResourceType = r.ResourceType,
                            ObjectName = r.ObjectName,
                            IndexName = r.IndexName,
                            LockMode = r.Mode,
                            CreateTime = DateTime.UtcNow,
                        });
                }
            }
            db.Session.CommitTransaction();
        }
        catch
        {
            db.Session.RollbackTransaction();
            throw;
        }
    }
}
