using Chloe;
using DBPilot.Common;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.Deadlocks;

/// <summary>
/// 死锁查询（列表/详情 + 趋势/指纹归并）：
/// 事件表（事实 + 原始 XML）+ 进程/资源维度表（分析）；趋势/归并纯 SQL 聚合（方言语句走 IPlatformDialect），
/// 列表摘要来自维度表，详情用 DeadlockReportParser 按存储的时区偏移重解析 XML。
/// </summary>
public class DeadlockService(IServiceProvider sp, IPlatformDialect dialect, ProviderRegistry registry) : IDepend
{
    /// <summary>
    /// 查询前置守卫（列表/筛选/趋势/归并共用）：解析平台库 DbContext（未配置返回拒绝文案）+
    /// 引擎能力守卫（死锁 XE 历史无对等数据源的引擎拒答，避免静默空集）。
    /// reject 为 null 时 db 必非空。
    /// </summary>
    private async Task<(string? Reject, DbContext Db)> RejectQueryAsync(DbContext? db, int instanceId)
    {
        if (db is null) return (InstanceConfigResolver.DbNotConfigured, null!);
        return (await InstanceConfigResolver.RejectUnsupportedAsync(db, registry, instanceId, DbpilotFeatures.DeadlockEvents), db);
    }

    /// <summary>
    /// 分页列表（时间范围按 event_time 过滤，倒序；摘要由维度表按页回查）。
    /// 可选过滤：指纹（归并卡点击）/ 锁类型 / 涉及对象模糊（资源维度）/ 登录名 / 主机名（进程维度）。
    /// </summary>
    public async Task<ServiceResult<PageList<DeadlockListItem>>> GetPageAsync(
        int instanceId, DateTime? from, DateTime? to, int page, int limit,
        string? fingerprint = null, string? lockType = null,
        string? objectName = null, string? loginName = null, string? hostName = null)
    {
        var (reject, db) = await RejectQueryAsync(sp.GetService<DbContext>(), instanceId);
        if (reject != null) return ServiceResult<PageList<DeadlockListItem>>.Failed(reject);

        var args = new
        {
            instanceId,
            start = from ?? new DateTime(2000, 1, 1),
            end = to ?? DateTime.MaxValue,
            fingerprint,
            lockType,
            objectName = string.IsNullOrEmpty(objectName) ? null : $"%{objectName.EscapeSqlLike()}%",
            loginName,
            hostName,
            skip = (page - 1) * limit,
            take = limit,
        };

        var conds = new List<string>();
        if (!string.IsNullOrEmpty(fingerprint))
            conds.Add("e.fingerprint = @fingerprint");
        if (lockType == "other")
            conds.Add("""
                EXISTS (SELECT 1 FROM dbpilot_deadlock_resource r
                        WHERE r.event_id = e.id AND r.resource_type NOT IN (N'keylock',N'objectlock',N'pagelock',N'ridlock'))
                """);
        else if (!string.IsNullOrEmpty(lockType))
            conds.Add("EXISTS (SELECT 1 FROM dbpilot_deadlock_resource r WHERE r.event_id = e.id AND r.resource_type = @lockType)");
        if (!string.IsNullOrEmpty(objectName))
            conds.Add(dialect.DeadlockObjectFilterSql());   // ESCAPE 字符随方言
        if (!string.IsNullOrEmpty(loginName))
            conds.Add("EXISTS (SELECT 1 FROM dbpilot_deadlock_process p WHERE p.event_id = e.id AND p.login_name = @loginName)");
        if (!string.IsNullOrEmpty(hostName))
            conds.Add("EXISTS (SELECT 1 FROM dbpilot_deadlock_process p WHERE p.event_id = e.id AND p.host_name = @hostName)");
        var condSql = conds.Count > 0 ? "AND " + string.Join(" AND ", conds) : "";

        var total = db.SqlQuery<int>(dialect.DeadlockPageCountSql(condSql), args).First();
        var rows = db.SqlQuery<DeadlockPageRow>(dialect.DeadlockPageRowsSql(condSql), args).ToList();

        var items = new List<DeadlockListItem>();
        foreach (var r in rows)
            items.Add(await ToListItemAsync(db, new DbpilotDeadlockEvent
            {
                Id = r.Id, InstanceId = instanceId, EventTime = r.EventTime,
                VictimSpids = r.VictimSpids, Fingerprint = r.Fingerprint,
            }));

        return ServiceResult<PageList<DeadlockListItem>>.Succeeded(
            PageList<DeadlockListItem>.Create(items, total, page, limit));
    }

    /// <summary>过滤下拉选项（进程维度 distinct；时间窗与列表一致）。</summary>
    public async Task<ServiceResult<DeadlockFilterOptions>> GetFilterOptionsAsync(int instanceId, DateTime? from, DateTime? to)
    {
        var (reject, db) = await RejectQueryAsync(sp.GetService<DbContext>(), instanceId);
        if (reject != null) return ServiceResult<DeadlockFilterOptions>.Failed(reject);

        var args = new
        {
            instanceId,
            start = from ?? new DateTime(2000, 1, 1),
            end = to ?? DateTime.MaxValue,
        };

        var logins = db.SqlQuery<string>(dialect.DeadlockFilterLoginsSql(), args).ToList();
        var hosts = db.SqlQuery<string>(dialect.DeadlockFilterHostsSql(), args).ToList();

        return ServiceResult<DeadlockFilterOptions>.Succeeded(new DeadlockFilterOptions { LoginNames = logins, HostNames = hosts });
    }

    /// <summary>详情：元信息 + 进程/资源结构化（关系图数据，原始 XML 重解析）。</summary>
    public async Task<ServiceResult<DeadlockDetail>> GetDetailAsync(int eventId)
    {
        var db = sp.GetService<DbContext>();
        if (db is null)
            return ServiceResult<DeadlockDetail>.Failed(InstanceConfigResolver.DbNotConfiguredFor("查询死锁"));

        var e = await db.Query<DbpilotDeadlockEvent>().FirstOrDefaultAsync(x => x.Id == eventId);
        if (e is null)
            return ServiceResult<DeadlockDetail>.Failed($"死锁事件 {eventId} 不存在");

        // 引擎能力守卫：事件所属实例不支持死锁读取时明确报错（与列表同口径）
        var reject = await InstanceConfigResolver.RejectUnsupportedAsync(db, registry, e.InstanceId, DbpilotFeatures.DeadlockEvents);
        if (reject != null) return ServiceResult<DeadlockDetail>.Failed(reject);

        var item = await ToListItemAsync(db, e);
        // XML 重解析出进程/资源/执行栈（事件表存的时区偏移还原 XML 内本地时间属性）
        var model = DeadlockReportParser.Parse(e.DeadlockGraph,
            DateTime.SpecifyKind(e.EventTime, DateTimeKind.Utc), TimeSpan.FromMinutes(e.UtcOffsetMinutes));

        return ServiceResult<DeadlockDetail>.Succeeded(new DeadlockDetail
        {
            Id = item.Id,
            EventTimeUtc = item.EventTimeUtc,
            VictimSpids = item.VictimSpids,
            VictimSummary = item.VictimSummary,
            OtherSummary = item.OtherSummary,
            Objects = item.Objects,
            Fingerprint = item.Fingerprint,
            InstanceId = e.InstanceId,
            Processes = model.Processes,
            Resources = model.Resources,
            GraphXml = e.DeadlockGraph,
        });
    }

    /// <summary>
    /// 锁类型趋势：纯 SQL 聚合 —— Total 按事件计（主表），分色 = 事件 × 涉及类型各计一次
    /// （资源表 COUNT(DISTINCT event_id)，同事件同类型多资源天然去重）；桶自适应 ≤6h 分钟 / ≤48h 小时 / 更长天。
    /// </summary>
    public async Task<ServiceResult<List<DeadlockTrendPoint>>> GetTrendAsync(int instanceId, DateTime? from, DateTime? to)
    {
        var (reject, db) = await RejectQueryAsync(sp.GetService<DbContext>(), instanceId);
        if (reject != null) return ServiceResult<List<DeadlockTrendPoint>>.Failed(reject);

        var start = from ?? DateTime.UtcNow.AddDays(-1);
        var end = to ?? DateTime.UtcNow;
        var unit = BucketUnit(end - start);
        var args = new { instanceId, start, end };

        var totals = db.SqlQuery<TrendTotalRow>(dialect.DeadlockTrendTotalsSql(unit), args).ToList();
        var colors = db.SqlQuery<DeadlockTrendPoint>(dialect.DeadlockTrendColorsSql(unit), args).ToList();

        // 合并：无资源的桶（维度表无行）也要出现，分色为 0
        var points = colors.ToDictionary(p => p.TimeUtc);
        foreach (var t in totals)
        {
            if (!points.TryGetValue(t.TimeUtc, out var p))
                points[t.TimeUtc] = p = new DeadlockTrendPoint { TimeUtc = t.TimeUtc };
            p.Total = t.Total;
        }
        return ServiceResult<List<DeadlockTrendPoint>>.Succeeded([.. points.Values.OrderBy(p => p.TimeUtc)]);
    }

    /// <summary>相似死锁归并 TOP：指纹 GROUP BY；对象/牺牲摘要取最近一次事件（维度表回查）。</summary>
    public async Task<ServiceResult<List<DeadlockFingerprintStat>>> GetFingerprintStatsAsync(int instanceId, DateTime? from, DateTime? to)
    {
        var (reject, db) = await RejectQueryAsync(sp.GetService<DbContext>(), instanceId);
        if (reject != null) return ServiceResult<List<DeadlockFingerprintStat>>.Failed(reject);

        var start = from ?? DateTime.UtcNow.AddDays(-1);
        var end = to ?? DateTime.UtcNow;

        var rows = db.SqlQuery<DeadlockFingerprintRow>(dialect.DeadlockFingerprintStatsSql(), new { instanceId, start, end }).ToList();

        if (rows.Count == 0) return ServiceResult<List<DeadlockFingerprintStat>>.Succeeded([]);

        var stats = new List<DeadlockFingerprintStat>();
        foreach (var r in rows)
        {
            var e = await db.Query<DbpilotDeadlockEvent>().FirstOrDefaultAsync(x => x.Id == r.LastEventId);
            var item = e is null ? null : await ToListItemAsync(db, e);
            stats.Add(new DeadlockFingerprintStat
            {
                Fingerprint = r.Fingerprint,
                Count = r.Count,
                FirstTimeUtc = DateTime.SpecifyKind(r.FirstTimeUtc, DateTimeKind.Utc),
                LastTimeUtc = DateTime.SpecifyKind(r.LastTimeUtc, DateTimeKind.Utc),
                Objects = item?.Objects ?? "",
                VictimSummary = item?.VictimSummary,
            });
        }
        return ServiceResult<List<DeadlockFingerprintStat>>.Succeeded(stats);
    }

    /// <summary>
    /// 死锁趋势-only 降级形态（deadlockEvents=none 引擎用）：读指标序列 Number of Deadlocks/sec
    /// 按桶均值聚合，不伪造事件计数；Chloe LINQ 直查，方言之争天然免疫。
    /// 不做引擎守卫——指标采集对所有引擎开放，没有数据自然全 null。
    /// </summary>
    public async Task<ServiceResult<DeadlockMetricsTrend>> GetMetricsTrendAsync(int instanceId, DateTime? from, DateTime? to)
    {
        var db = sp.GetService<DbContext>();
        if (db is null)
            return ServiceResult<DeadlockMetricsTrend>.Failed(InstanceConfigResolver.DbNotConfiguredFor("查询死锁"));

        var start = from ?? DateTime.UtcNow.AddDays(-1);
        var end = to ?? DateTime.UtcNow;

        var rows = await db.Query<DbpilotInstanceMetrics>()
            .Where(x => x.InstanceId == instanceId && x.SampleTime >= start && x.SampleTime < end)
            .OrderBy(x => x.SampleTime)
            .ToListAsync();

        var step = InstanceMetricsQueryService.AutoBucketSeconds(end - start);
        var buckets = InstanceMetricsQueryService.BuildBuckets(start, end, step);
        var series = InstanceMetricsQueryService.AvgByBucket(rows, buckets, step, x => x.SampleTime, x => x.DeadlocksPerSec);

        return ServiceResult<DeadlockMetricsTrend>.Succeeded(new DeadlockMetricsTrend
        {
            BucketSeconds = step,
            Times = buckets,
            DeadlocksPerSec = series,
        });
    }

    /// <summary>桶粒度自适应（对齐慢SQL口径加一档天）：≤6h 分钟 / ≤48h 小时 / 更长天。</summary>
    internal static string BucketUnit(TimeSpan span)
        => span <= TimeSpan.FromHours(6) ? "minute" : span <= TimeSpan.FromHours(48) ? "hour" : "day";

    /// <summary>列表项：事件 + 维度表行组装摘要（登录@主机（应用）/ 涉及对象）。</summary>
    private static async Task<DeadlockListItem> ToListItemAsync(DbContext db, DbpilotDeadlockEvent e)
    {
        var processes = await db.Query<DbpilotDeadlockProcess>().Where(x => x.EventId == e.Id).ToListAsync();
        var resources = await db.Query<DbpilotDeadlockResource>().Where(x => x.EventId == e.Id).ToListAsync();

        static string Who(DbpilotDeadlockProcess p) => p.LoginName.FmtWho(p.HostName, p.ClientApp);

        var victims = processes.Where(p => p.IsVictim).ToList();
        var others = processes.Where(p => !p.IsVictim).ToList();

        return new DeadlockListItem
        {
            Id = e.Id,
            EventTimeUtc = DateTime.SpecifyKind(e.EventTime, DateTimeKind.Utc),
            VictimSpids = e.VictimSpids,
            VictimSummary = victims.Count > 0 ? string.Join(" ｜ ", victims.Select(Who)) : null,
            OtherSummary = others.Count > 0 ? string.Join(" ｜ ", others.Select(Who)) : null,
            ProcessCount = processes.Count,
            Objects = string.Join(" ｜ ", resources.Select(Describe).Where(s => !string.IsNullOrEmpty(s)).Distinct()),
            Fingerprint = e.Fingerprint,
        };
    }

    /// <summary>资源摘要：对象名（索引）优先，无对象名的资源类型归中文描述。</summary>
    private static string Describe(DbpilotDeadlockResource r)
    {
        if (!string.IsNullOrEmpty(r.ObjectName))
            return string.IsNullOrEmpty(r.IndexName) ? r.ObjectName! : $"{r.ObjectName}（{r.IndexName}）";
        return TypeDesc(r.ResourceType);
    }

    private static string TypeDesc(string t) => t.ToLowerInvariant() switch
    {
        "keylock" => "KEY 锁",
        "objectlock" => "OBJECT 锁",
        "pagelock" => "PAGE 锁",
        "ridlock" => "RID 锁",
        "exchangeevent" => "并行交换",
        "threadlock" => "线程（threadlock）",
        _ => t,
    };
}
