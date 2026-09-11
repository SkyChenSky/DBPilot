using Chloe;
using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Collecting;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.Providers;
using DBPilot.Core.TopSql;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DBPilot.Core.QueryPlan;

/// <summary>
/// 计划快照采集（QueryPlanJob 每 5 分钟调用）：
/// dm_exec_query_stats 按 (指纹, query_plan_hash) 聚合的缓存累计快照落 dbpilot_query_plan，
/// 同指纹出现新 plan_hash 时记 dbpilot_plan_change 变更事件（老/新计划各自累计均值，检测时固化）。
/// 规则：
/// ① 指纹首见（库中无该指纹任何行）→ 种子（插库不产事件，防部署首日事件洪水）；
/// ② 已知指纹 + 新 plan_hash → 变更事件（old = 该指纹 last_seen 最大的现有计划）；
/// ③ 已存在 plan_hash 且计数器前进（本窗口执行过）→ 刷新 last_seen + 累计值；累计未动的
///    休眠计划不发 UPDATE（差量语义）——last_seen_utc 由此承载「最后一次执行时刻」；
/// ④ 实例重启 / 计划驱逐重建：同形状计划 query_plan_hash 不变 → 天然不产假事件；
/// ⑤ 计划内容只在首见新 plan_hash 时抓一份（驱逐前抢救）：dm_exec_query_plan XML 优先，
///    NULL 时兜 dm_exec_text_query_plan 语句级文本，超 1MB 存 NULL。
/// 噪音排除与 TopSQL 差值采集同口径（TopSqlFilter：配置模式 + 指纹黑名单）。
/// 并发读被监控实例、平台库写串行（Chloe 上下文非线程安全）。
/// </summary>
public class QueryPlanCollectService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    CollectStateStore states, CollectOptions options, TopSqlExcludeOptions excludes) : IDepend
{
    private const int BatchSize = 500;

    /// <summary>计划 XML 落库上限（字符），超限存 NULL。</summary>
    internal const int MaxPlanXmlChars = 1_000_000;

    /// <summary>主采集入口：加载全部启用实例并发采计划快照、平台库串行落库（含计划变更事件判定）。</summary>
    public async Task CollectAllAsync(CancellationToken ct = default)
    {
        var entities = await CollectRunner.LoadEnabledAsync(sp);
        if (entities is null) return;
        var db = sp.GetRequiredService<DbContext>();

        var filter = new TopSqlFilter
        {
            Patterns = excludes.Patterns,
            Fingerprints = await TopSqlService.LoadExcludedFingerprintsAsync(db),
        };

        var collected = new ConcurrentBag<(DbpilotInstance Entity, List<QueryPlanRawRow> Rows)>();

        await CollectRunner.ForEachEnabledAsync(entities, states, options, "计划快照采集", ct, async (e, token) =>
        {
            var state = states.Of(e.Id, "计划快照采集");
            var cfg = InstanceConfigResolver.ToConfig(crypto, e);
            var rows = await provider.GetQueryPlanStatsAsync(cfg, filter, token);
            state.OnSuccess();
            if (rows.Count > 0)
                collected.Add((e, rows));
        });

        // 平台库写串行：种子/变更检测 + XML 抓取 + 落库
        await CollectRunner.PersistAsync("计划快照落库失败", async () =>
        {
            foreach (var (entity, rows) in collected)
                await PersistAsync(db, entity, rows);
        });
    }

    private async Task PersistAsync(DbContext db, DbpilotInstance entity, List<QueryPlanRawRow> rows)
    {
        var now = DateTime.UtcNow;

        // 单事务：每条语句自动提交时 375 行刷新 ≈ 千条语句突发（COMMIT/SET autocommit
        // 各占一条），平台库与被监控实例同机（自监控环）时会整体计入该实例 QPS，
        // 呈每 5 分钟一轮的尖峰——合事务后只剩 UPDATE 本身
        db.Session.BeginTransaction();
        try
        {

            // 现有计划轻量投影（不含 plan_xml 大列）
            var existing = await db.Query<DbpilotQueryPlan>()
                .Where(x => x.InstanceId == entity.Id)
                .Select(x => new DbpilotQueryPlan
                {
                    Id = x.Id,
                    InstanceId = x.InstanceId,
                    Fingerprint = x.Fingerprint,
                    DbName = x.DbName,
                    QueryPlanHash = x.QueryPlanHash,
                    CompileTimeUtc = x.CompileTimeUtc,
                    FirstSeenUtc = x.FirstSeenUtc,
                    LastSeenUtc = x.LastSeenUtc,
                    ExecutionCount = x.ExecutionCount,
                    TotalElapsedMs = x.TotalElapsedMs,
                    TotalWorkerMs = x.TotalWorkerMs,
                    TotalLogicalReads = x.TotalLogicalReads,
                })
                .ToListAsync();

            var (inserts, updates, events) = DetectPlanChanges(entity.Id, existing, rows, now);

            // 新计划抓 XML（驱逐前抢救；XML 缺失兜语句级文本计划，驱逐/超 1MB 的键取不到 → 保持 NULL）
            if (inserts.Count > 0)
            {
                var cfg = InstanceConfigResolver.ToConfig(crypto, entity);
                var xmls = await provider.GetQueryPlanXmlsAsync(cfg, inserts
                    .Select(x => new QueryPlanHandleRef(x.PlanHandleHex ?? "", x.StatementStartOffset, x.StatementEndOffset))
                    .ToList());
                foreach (var p in inserts)
                {
                    var key = new QueryPlanHandleRef(p.PlanHandleHex ?? "", p.StatementStartOffset, p.StatementEndOffset).Key;
                    if (xmls.TryGetValue(key, out var xml) && xml.Length <= MaxPlanXmlChars)
                        p.PlanXml = xml;
                }
            }

            for (var i = 0; i < inserts.Count; i += BatchSize)
                await db.InsertRangeAsync(inserts.Skip(i).Take(BatchSize).ToList());

            // 局部更新：只 SET 统计列，不触碰 plan_xml（投影未载，全实体更新会将其刷成 NULL）
            foreach (var u in updates)
                await db.UpdateAsync<DbpilotQueryPlan>(x => x.Id == u.Id, x => new DbpilotQueryPlan
                {
                    LastSeenUtc = u.LastSeenUtc,
                    ExecutionCount = u.ExecutionCount,
                    TotalElapsedMs = u.TotalElapsedMs,
                    TotalWorkerMs = u.TotalWorkerMs,
                    TotalLogicalReads = u.TotalLogicalReads,
                    CompileTimeUtc = u.CompileTimeUtc,
                });

            for (var i = 0; i < events.Count; i += BatchSize)
                await db.InsertRangeAsync(events.Skip(i).Take(BatchSize).ToList());

            if (inserts.Count > 0 || events.Count > 0)
                Log.Information("实例 {Id} 计划快照：新增 {Inserts} 个计划、{Events} 条变更事件、刷新 {Updates} 行",
                    entity.Id, inserts.Count, events.Count, updates.Count);

            db.Session.CommitTransaction();
        }
        catch
        {
            db.Session.RollbackTransaction();
            throw;
        }
    }

    /// <summary>
    /// 变更检测（纯函数，供单测）：raw 行按 (指纹, plan_hash) 合并（多 handle 取首、计数求和、creation 取 MIN）后，
    /// 与库中现有计划比对。返回（新计划插入行、已有计划刷新行、变更事件）。
    /// </summary>
    internal static (List<DbpilotQueryPlan> Inserts, List<DbpilotQueryPlan> Updates, List<DbpilotPlanChange> Events)
        DetectPlanChanges(int instanceId, List<DbpilotQueryPlan> existing, List<QueryPlanRawRow> rows, DateTime now)
    {
        // 同 (指纹, plan_hash) 多 handle：计数求和、creation 取 MIN、handle 取首
        var merged = rows
            .GroupBy(r => $"{r.Fingerprint}|{r.QueryPlanHash}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new DbpilotQueryPlan
            {
                InstanceId = instanceId,
                Fingerprint = g.First().Fingerprint,
                DbName = g.Select(r => r.DbName).FirstOrDefault(x => x != null),
                QueryPlanHash = g.First().QueryPlanHash,
                PlanHandleHex = g.First().PlanHandleHex,
                // 同组语句偏移恒同值，取 First 即确定值；透传给 XML/文本抓取做语句级寻址
                StatementStartOffset = g.First().StatementStartOffset,
                StatementEndOffset = g.First().StatementEndOffset,
                // nullable Min：空序列返回 null（DefaultIfEmpty 会给 0001-01-01，SQL datetime 溢出）
                CompileTimeUtc = g.Select(r => r.CreationTimeUtc).Min(),
                FirstSeenUtc = now,
                LastSeenUtc = now,
                ExecutionCount = g.Sum(r => r.ExecutionCount),
                TotalElapsedMs = SqlConverts.UsToMs(g.Sum(r => r.TotalElapsedUs)),
                TotalWorkerMs = SqlConverts.UsToMs(g.Sum(r => r.TotalWorkerUs)),
                TotalLogicalReads = g.Sum(r => r.TotalLogicalReads),
                CreateTime = DateTime.UtcNow,
            })
            .ToList();

        var existingByFp = existing
            .GroupBy(x => x.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var inserts = new List<DbpilotQueryPlan>();
        var updates = new List<DbpilotQueryPlan>();
        var events = new List<DbpilotPlanChange>();

        foreach (var m in merged)
        {
            if (!existingByFp.TryGetValue(m.Fingerprint, out var fpPlans))
            {
                inserts.Add(m);   // ① 指纹首见：种子，不产事件
                continue;
            }

            var match = fpPlans.FirstOrDefault(x =>
                string.Equals(x.QueryPlanHash, m.QueryPlanHash, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                // ③ 计数器前进（本窗口真实执行过）→ 刷新累计 + last_seen；累计未动 = 计划在缓存中
                //    休眠 → 不发 UPDATE（差量语义与 TopSQL 差值采集同源：行仍在缓存 ≠ 有活动，
                //    无差别刷新会让全部行每轮弄脏）。last_seen 由此承载「最后执行时刻」
                var earlierCompile = m.CompileTimeUtc != null
                    && (match.CompileTimeUtc == null || m.CompileTimeUtc < match.CompileTimeUtc);
                var advanced = m.ExecutionCount != match.ExecutionCount
                    || m.TotalElapsedMs != match.TotalElapsedMs
                    || m.TotalWorkerMs != match.TotalWorkerMs
                    || m.TotalLogicalReads != match.TotalLogicalReads
                    || earlierCompile;
                if (!advanced) continue;
                match.LastSeenUtc = now;
                match.ExecutionCount = m.ExecutionCount;
                match.TotalElapsedMs = m.TotalElapsedMs;
                match.TotalWorkerMs = m.TotalWorkerMs;
                match.TotalLogicalReads = m.TotalLogicalReads;
                if (m.CompileTimeUtc != null && (match.CompileTimeUtc == null || m.CompileTimeUtc < match.CompileTimeUtc))
                    match.CompileTimeUtc = m.CompileTimeUtc;
                updates.Add(match);
                continue;
            }

            // ② 新 plan_hash：变更事件（old = 该指纹 last_seen 最大的现有计划）+ 新计划插入
            var old = fpPlans.OrderByDescending(x => x.LastSeenUtc).First();
            events.Add(new DbpilotPlanChange
            {
                InstanceId = instanceId,
                Fingerprint = m.Fingerprint,
                DbName = m.DbName ?? old.DbName,
                OldPlanHash = old.QueryPlanHash,
                NewPlanHash = m.QueryPlanHash,
                OldAvgElapsedMs = Avg(old.TotalElapsedMs, old.ExecutionCount),
                NewAvgElapsedMs = Avg(m.TotalElapsedMs, m.ExecutionCount),
                OldAvgWorkerMs = Avg(old.TotalWorkerMs, old.ExecutionCount),
                NewAvgWorkerMs = Avg(m.TotalWorkerMs, m.ExecutionCount),
                OldAvgReads = Avg(old.TotalLogicalReads, old.ExecutionCount),
                NewAvgReads = Avg(m.TotalLogicalReads, m.ExecutionCount),
                OldExecCount = old.ExecutionCount,
                NewExecCount = m.ExecutionCount,
                ChangedAtUtc = m.CompileTimeUtc ?? now,
                CreateTime = DateTime.UtcNow,
            });
            inserts.Add(m);
        }

        return (inserts, updates, events);
    }

    /// <summary>累计总量均值（除零置 NULL）。</summary>
    internal static long? Avg(long total, long count) => count > 0 ? total / count : null;
}
