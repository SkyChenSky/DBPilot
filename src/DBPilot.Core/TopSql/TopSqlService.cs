using Chloe;
using DBPilot.Common;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.TopSql;

/// <summary>
/// 实时 Top SQL（按需查询不走定时 Job）：
/// dm_exec_query_stats 实例启动以来累计快照 → μs→ms / 平均值 → 按口径排序取 TopN。
/// 历史 Top SQL（差值入库）由定时 Job 承担。
/// 指纹黑名单（dbpilot_top_sql_exclusion）维护亦在此：查询前自动加载并入排除条件。
/// </summary>
public class TopSqlService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider, IPlatformDialect dialect) : IDepend
{
    /// <summary>实例运行不足 30 天 → DMV 累计值不完整（重启清零），结果附警示（与索引诊断各自声明，语义同源）。</summary>
    private const int DataIncompleteDays = 30;
    private const int TopNCeiling = 200;

    /// <summary>metric：avg=平均耗时、total=总耗时（默认）；topN 默认 20、上限 200；
    /// filter.ExcludeSystemDb / Patterns 由 Controller 从配置构建；Fingerprints 本方法内部自动加载。</summary>
    public async Task<ServiceResult<TopSqlRealtimeResult>> GetRealtimeAsync(int instanceId, string? db, string? metric, int? topN, TopSqlFilter? filter = null)
    {
        var (err, entity) = await InstanceConfigResolver.LoadAsync(sp, instanceId);
        if (err != null) return ServiceResult<TopSqlRealtimeResult>.Failed(err);

        filter ??= new TopSqlFilter();
        filter.Fingerprints = await LoadExcludedFingerprintsAsync();

        List<TopSqlRawRow> rows;
        try
        {
            rows = await provider.GetTopSqlRealtimeAsync(InstanceConfigResolver.ToConfig(crypto, entity!), db ?? "", filter);
        }
        catch (Exception ex)
        {
            return ServiceResult<TopSqlRealtimeResult>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }

        var m = metric == "total" ? "total" : "avg";
        var n = Math.Clamp(topN ?? 20, 1, TopNCeiling);

        // 占比分母 = 展示的 TopN 行自身合计（对齐阿里云：TopN 变化占比随之变化），故先截取再算
        var topItems = OrderByMetric(BuildItems(rows), m).Take(n).ToList();
        AttachPercents(topItems);

        var result = new TopSqlRealtimeResult
        {
            Metric = m,
            TopN = n,
            Items = topItems,
            SnapshotTime = DateTime.UtcNow,
            InstanceStartTimeUtc = SqlConverts.SpecifyUtc(entity!.SqlServerStartTime),
            DataIncomplete = entity.SqlServerStartTime is { } t
                && (DateTime.UtcNow - DateTime.SpecifyKind(t, DateTimeKind.Utc)).TotalDays < DataIncompleteDays
        };
        return ServiceResult<TopSqlRealtimeResult>.Succeeded(result);
    }

    /// <summary>
    /// 历史 Top SQL（总榜口径）：dbpilot_top_sql_delta 全量按（指纹, 库）聚合（SUM 差值 / MAX 分钟峰值，
    /// 数据范围 = Housekeeping TopSqlDeltaDays 保留期内），语句文本取 dbpilot_sql_template；
    /// 指纹黑名单后加入的同样隐藏（NOT EXISTS）。
    /// metric = total|avg|count|cpu|reads（排序口径，默认 total）；db 为空 = 全部库。
    /// </summary>
    public ServiceResult<TopSqlHistoryResult> GetHistory(int instanceId, string? db, string? metric, int? topN, bool excludeSystemDb = false)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<TopSqlHistoryResult>.Failed(InstanceConfigResolver.DbNotConfigured);

        var m = metric is "avg" or "count" or "cpu" or "reads" ? metric : "total";
        var n = Math.Clamp(topN ?? 20, 1, TopNCeiling);

        // SQL 内排序（avg 口径在库端除，避免拉全量到内存）
        var orderBy = m switch
        {
            "avg" => "CASE WHEN q.ExecutionCount = 0 THEN 0 ELSE q.TotalElapsedMs / q.ExecutionCount END",
            "count" => "q.ExecutionCount",
            "cpu" => "q.TotalWorkerMs",
            "reads" => "q.TotalLogicalReads",
            _ => "q.TotalElapsedMs",
        };

        List<TopSqlHistoryRawRow> rows;
        try
        {
            rows = dbc.SqlQuery<TopSqlHistoryRawRow>(dialect.TopSqlHistorySql(orderBy, n),
                new { instanceId, db = db ?? "", excludeSystemDb });
        }
        catch (Exception ex)
        {
            return ServiceResult<TopSqlHistoryResult>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }

        // 占比分母 = 展示的 TopN 行自身合计（口径同实时页），先截取再算
        var items = rows.Select(r => new TopSqlHistoryItem
        {
            Fingerprint = r.Fingerprint,
            DbName = r.DbName,
            SqlText = r.SqlText ?? "",
            ExecutionCount = r.ExecutionCount,
            TotalElapsedMs = r.TotalElapsedMs,
            AvgElapsedMs = r.ExecutionCount == 0 ? 0 : Math.Round(r.TotalElapsedMs * 1.0 / r.ExecutionCount, 3),
            TotalCpuMs = r.TotalWorkerMs,
            AvgCpuMs = r.ExecutionCount == 0 ? 0 : Math.Round(r.TotalWorkerMs * 1.0 / r.ExecutionCount, 3),
            TotalLogicalReads = r.TotalLogicalReads,
            TotalPhysicalReads = r.TotalPhysicalReads,
            TotalWrites = r.TotalWrites,
            MaxElapsedMs = r.MaxElapsedMs,
            FirstSeenUtc = DateTime.SpecifyKind(r.FirstSeen, DateTimeKind.Utc),
            LastSeenUtc = DateTime.SpecifyKind(r.LastSeen, DateTimeKind.Utc),
        }).ToList();
        AttachPercents(items);

        return ServiceResult<TopSqlHistoryResult>.Succeeded(new TopSqlHistoryResult
        {
            Metric = m,
            TopN = n,
            Items = items,
        });
    }

    /// <summary>指纹黑名单列表（页面“已排除”管理弹窗）。</summary>
    public async Task<ServiceResult<List<DbpilotTopSqlExclusion>>> GetExclusionsAsync()
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<List<DbpilotTopSqlExclusion>>.Failed(InstanceConfigResolver.DbNotConfigured);
        var list = await dbc.Query<DbpilotTopSqlExclusion>().OrderByDesc(x => x.CreatedAt).ToListAsync();
        return ServiceResult<List<DbpilotTopSqlExclusion>>.Succeeded(list);
    }

    /// <summary>加入指纹黑名单（fingerprint 必须为 hex；sqlHead 为备注片段；已存在则幂等成功）。</summary>
    public async Task<ServiceResult<bool>> AddExclusionAsync(string fingerprint, string? sqlHead)
    {
        fingerprint = (fingerprint ?? "").Trim().ToLowerInvariant();
        if (fingerprint.IsNullOrEmpty() || !IsHex(fingerprint))
            return ServiceResult<bool>.Failed("指纹格式不合法（应为十六进制）");

        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<bool>.Failed(InstanceConfigResolver.DbNotConfigured);

        var exists = await dbc.Query<DbpilotTopSqlExclusion>().AnyAsync(x => x.Fingerprint == fingerprint);
        if (!exists)
        {
            dbc.Insert(new DbpilotTopSqlExclusion
            {
                Fingerprint = fingerprint,
                SqlHead = sqlHead?.Trim().Sub(500),
                CreatedAt = DateTime.Now,
            });
        }
        return ServiceResult<bool>.Succeeded(true);
    }

    /// <summary>移出指纹黑名单（恢复显示）。</summary>
    public async Task<ServiceResult<bool>> RemoveExclusionAsync(int id)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<bool>.Failed(InstanceConfigResolver.DbNotConfigured);

        dbc.Delete<DbpilotTopSqlExclusion>(x => x.Id == id);
        return await Task.FromResult(ServiceResult<bool>.Succeeded(true));
    }

    private async Task<List<string>> LoadExcludedFingerprintsAsync()
        => await LoadExcludedFingerprintsAsync(sp.GetService<DbContext>());

    /// <summary>指纹黑名单加载（实时查询与差值采集共用）。</summary>
    internal static async Task<List<string>> LoadExcludedFingerprintsAsync(DbContext? db)
    {
        if (db is null) return [];
        var list = await db.Query<DbpilotTopSqlExclusion>().ToListAsync();
        return list.Select(x => x.Fingerprint).ToList();
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return s.Length is 16 or (> 31 and < 65);
    }

    /// <summary>原始行 → 展示行：μs→ms（/1000）、平均值（执行次数 0 防除零）、文本截 4000、时间标 UTC。</summary>
    public static List<TopSqlRealtimeItem> BuildItems(List<TopSqlRawRow> rows)
        => rows.Select(r => new TopSqlRealtimeItem
        {
            Fingerprint = r.Fingerprint,
            DbName = r.DbName,
            SqlText = (r.SqlText ?? "").Trim().Sub(4000),
            FullSqlText = (r.FullSqlText ?? "").Trim().Sub(4000),
            ExecutionCount = r.ExecutionCount,
            TotalElapsedMs = UsToMs(r.TotalElapsedUs),
            AvgElapsedMs = r.ExecutionCount == 0 ? 0 : UsToMs(r.TotalElapsedUs) / r.ExecutionCount,
            TotalCpuMs = UsToMs(r.TotalWorkerUs),
            AvgCpuMs = r.ExecutionCount == 0 ? 0 : UsToMs(r.TotalWorkerUs) / r.ExecutionCount,
            TotalLogicalReads = r.TotalLogicalReads,
            TotalPhysicalReads = r.TotalPhysicalReads,
            LastExecutionTimeUtc = SqlConverts.SpecifyUtc(r.LastExecutionTime),
        }).ToList();

    /// <summary>排序口径：avg = 平均耗时降序；其余（含非法值）= 总耗时降序。</summary>
    public static IEnumerable<TopSqlRealtimeItem> OrderByMetric(IEnumerable<TopSqlRealtimeItem> items, string metric)
        => metric == "avg"
            ? items.OrderByDescending(x => x.AvgElapsedMs)
            : items.OrderByDescending(x => x.TotalElapsedMs);

    /// <summary>占比（实时/历史共用，对齐阿里云展示）：分母 = 展示的 TopN 行自身合计，TopN 内占比之和 = 100%。保留两位小数，零安全。</summary>
    internal static void AttachPercents<T>(List<T> items) where T : ITopSqlPercentRow
    {
        static double Pct(double part, double total) => total == 0 ? 0 : Math.Round(part * 100.0 / total, 2);

        var countSum = (double)items.Sum(x => x.ExecutionCount);
        var elapsedSum = items.Sum(x => x.TotalElapsedMs);
        var cpuSum = items.Sum(x => x.TotalCpuMs);
        var readsSum = (double)items.Sum(x => x.TotalLogicalReads);

        foreach (var x in items)
        {
            x.ExecutionCountPercent = Pct(x.ExecutionCount, countSum);
            x.TotalElapsedPercent = Pct(x.TotalElapsedMs, elapsedSum);
            x.TotalCpuPercent = Pct(x.TotalCpuMs, cpuSum);
            x.LogicalReadsPercent = Pct(x.TotalLogicalReads, readsSum);
        }
    }

    /// <summary>µs → ms 展示版（保留 3 位小数；落库整数毫秒版见 SqlConverts.UsToMs）。</summary>
    private static double UsToMs(long us) => Math.Round(us / 1000.0, 3);
}
