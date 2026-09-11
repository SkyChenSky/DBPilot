using Chloe;
using DBPilot.Common;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.SlowSql;

/// <summary>慢SQL明细行（文本截断预览，全文走单条详情）。</summary>
public class SlowSqlItem
{
    public int Id { get; set; }
    public DateTime EventTimeUtc { get; set; }
    public string? DbName { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? AppName { get; set; }
    public int? SessionId { get; set; }

    /// <summary>1=rpc 2=batch</summary>
    public int SqlType { get; set; }

    public long DurationMs { get; set; }
    public long? CpuMs { get; set; }
    public long? LogicalReads { get; set; }
    public long? PhysicalReads { get; set; }
    public long? Writes { get; set; }
    public long? RowCount { get; set; }
    public string? Fingerprint { get; set; }

    /// <summary>前 200 字符预览（空白折叠）。</summary>
    public string SqlPreview { get; set; } = string.Empty;
}

/// <summary>慢SQL单条全文。</summary>
public class SlowSqlDetail : SlowSqlItem
{
    public string SqlText { get; set; } = string.Empty;
}

/// <summary>模板聚合行（阿里云口径：耗时/CPU/读写的 总/平均/最大 + 耗时占比）。</summary>
public class SlowSqlTemplate
{
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>样例 SQL（前 500 字符）。</summary>
    public string SampleSql { get; set; } = string.Empty;

    /// <summary>样例库名（字典序最大，指纹跨库时收敛为一个代表值）。</summary>
    public string? DbName { get; set; }

    public int Count { get; set; }

    /// <summary>耗时比例（本指纹总耗时 / 全部慢SQL总耗时，%）。</summary>
    public decimal? TotalRatio { get; set; }

    public long AvgMs { get; set; }
    public long MaxMs { get; set; }
    public long? CpuTotalMs { get; set; }
    public long? CpuAvgMs { get; set; }
    public long? CpuMaxMs { get; set; }
    public decimal? RowsAvg { get; set; }
    public long? RowsMax { get; set; }
    public long? ReadsTotal { get; set; }
    public decimal? ReadsAvg { get; set; }
    public long? ReadsMax { get; set; }
    public long? PreadsTotal { get; set; }
    public decimal? PreadsAvg { get; set; }
    public long? PreadsMax { get; set; }
    public long? WritesTotal { get; set; }
    public decimal? WritesAvg { get; set; }
    public long? WritesMax { get; set; }
}

/// <summary>趋势点（条数 + 总耗时）。</summary>
public class SlowSqlTrendPoint
{
    public DateTime TimeUtc { get; set; }
    public int Count { get; set; }
    public long TotalMs { get; set; }
}

/// <summary>
/// 慢SQL查询：明细分页 / 模板聚合 / 趋势。
/// 明细用 ExpressionBuilder 强类型条件；聚合与趋势原生 SQL（GROUP BY 粒度自适应：≤6h 分钟、否则小时）。
/// </summary>
public class SlowSqlService(IServiceProvider sp, IPlatformDialect dialect, ProviderRegistry registry) : IDepend
{
    /// <summary>明细分页（duration 降序；db/minDurationMs/fingerprint/excludeSystemDb 筛选；系统库口径对齐 Top SQL）。</summary>
    public async Task<PageList<SlowSqlItem>?> GetPageAsync(
        int instanceId, DateTime? from, DateTime? to, string? db, long? minDurationMs, string? fingerprint,
        bool excludeSystemDb, int page, int limit)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return null;

        var cond = ExpressionBuilder.Init<DbpilotSlowSql>().And(x => x.InstanceId == instanceId);
        if (from != null) cond = cond.And(x => x.EventTime >= from);
        if (to != null) cond = cond.And(x => x.EventTime < to);
        if (!string.IsNullOrWhiteSpace(db)) cond = cond.And(x => x.DbName == db);
        if (minDurationMs is > 0) cond = cond.And(x => x.DurationMs >= minDurationMs);
        if (!string.IsNullOrWhiteSpace(fingerprint)) cond = cond.And(x => x.Fingerprint == fingerprint);
        if (excludeSystemDb && string.IsNullOrWhiteSpace(db))
            cond = cond.And(x => x.DbName != "master" && x.DbName != "model" && x.DbName != "msdb" && x.DbName != "tempdb");

        var pageList = await dbc.Query<DbpilotSlowSql>()
            .Where(cond)
            .OrderByDesc(x => x.EventTime)
            .PageListAsync(page, limit);

        return PageList<SlowSqlItem>.Create(
            pageList.Items.Select(ToItem),
            pageList.Total, pageList.PageIndex, pageList.PageSize);
    }

    /// <summary>单条全文。</summary>
    public async Task<SlowSqlDetail?> GetDetailAsync(int rowId)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return null;

        var e = await dbc.Query<DbpilotSlowSql>().FirstOrDefaultAsync(x => x.Id == rowId);
        if (e is null) return null;

        var item = ToItem(e);
        return new SlowSqlDetail { Id = item.Id, EventTimeUtc = item.EventTimeUtc, DbName = item.DbName, LoginName = item.LoginName, HostName = item.HostName, AppName = item.AppName, SessionId = item.SessionId, SqlType = item.SqlType, DurationMs = item.DurationMs, CpuMs = item.CpuMs, LogicalReads = item.LogicalReads, PhysicalReads = item.PhysicalReads, Writes = item.Writes, RowCount = item.RowCount, Fingerprint = item.Fingerprint, SqlPreview = item.SqlPreview, SqlText = e.SqlText };
    }

    /// <summary>
    /// 模板聚合 Top（按总耗时降序，默认 Top 50）。
    /// 降级形态（能力矩阵 slowSqlEvents=none 的引擎，如 PostgreSQL）：改读 dbpilot_top_sql_delta
    /// 按指纹聚合的近似模板榜（Σ耗时/Σ次数口径，无单次上限/行数列）——绝不伪造 dbpilot_slow_sql 事件行。
    /// </summary>
    public async Task<List<SlowSqlTemplate>?> GetTemplatesAsync(
        int instanceId, DateTime? from, DateTime? to, string? db, long? minDurationMs, bool excludeSystemDb)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return null;

        var (_, entity) = await InstanceConfigResolver.LoadAsync(sp, instanceId);
        if (registry.CapabilityOf(entity?.Engine, DbpilotCapabilityKeys.SlowSqlEvents) == DbpilotCapabilityLevel.None)
        {
            var dwhere = "WHERE d.instance_id = @instanceId"
                + (from != null ? " AND d.window_start >= @from" : "")
                + (to != null ? " AND d.window_start < @to" : "")
                + (string.IsNullOrWhiteSpace(db) ? "" : " AND d.db_name = @db")
                + (excludeSystemDb && string.IsNullOrWhiteSpace(db) ? " AND d.db_name NOT IN ('master','model','msdb','tempdb')" : "");
            return dbc.SqlQuery<SlowSqlTemplate>(dialect.SlowSqlTemplatesFromTopSqlSql(dwhere),
                new { instanceId, from, to, db }).ToList();
        }

        var where = "WHERE instance_id = @instanceId"
            + (from != null ? " AND event_time >= @from" : "")
            + (to != null ? " AND event_time < @to" : "")
            + (string.IsNullOrWhiteSpace(db) ? "" : " AND db_name = @db")
            + (excludeSystemDb && string.IsNullOrWhiteSpace(db) ? " AND db_name NOT IN ('master','model','msdb','tempdb')" : "")
            + (minDurationMs is > 0 ? " AND duration_ms >= @minDurationMs" : "");

        return dbc.SqlQuery<SlowSqlTemplate>(dialect.SlowSqlTemplatesSql(where),
            new { instanceId, from, to, db, minDurationMs }).ToList();
    }

    /// <summary>趋势（条数/总耗时；粒度自适应：区间 ≤6h 按分钟，否则按小时；筛选口径与明细一致）。</summary>
    public async Task<List<SlowSqlTrendPoint>?> GetTrendAsync(
        int instanceId, DateTime? from, DateTime? to, string? db, long? minDurationMs, string? fingerprint,
        bool excludeSystemDb)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return null;

        var start = from ?? DateTime.UtcNow.AddDays(-1);
        var end = to ?? DateTime.UtcNow;
        var unit = end - start <= TimeSpan.FromHours(6) ? "minute" : "hour";
        var conds =
            (string.IsNullOrWhiteSpace(db) ? "" : "AND db_name = @db\n")
            + (excludeSystemDb && string.IsNullOrWhiteSpace(db) ? "AND db_name NOT IN ('master','model','msdb','tempdb')\n" : "")
            + (minDurationMs is > 0 ? "AND duration_ms >= @minDurationMs\n" : "")
            + (string.IsNullOrWhiteSpace(fingerprint) ? "" : "AND fingerprint = @fingerprint");

        return dbc.SqlQuery<SlowSqlTrendPoint>(dialect.SlowSqlTrendSql(unit, conds),
            new { instanceId, start, end, db, minDurationMs, fingerprint }).ToList();
    }

    private static SlowSqlItem ToItem(DbpilotSlowSql e) => new()
    {
        Id = e.Id,
        EventTimeUtc = DateTime.SpecifyKind(e.EventTime, DateTimeKind.Utc),
        DbName = e.DbName,
        LoginName = e.LoginName,
        HostName = e.HostName,
        AppName = e.AppName,
        SessionId = e.SessionId,
        SqlType = e.SqlType,
        DurationMs = e.DurationMs,
        CpuMs = e.CpuMs,
        LogicalReads = e.LogicalReads,
        PhysicalReads = e.PhysicalReads,
        Writes = e.Writes,
        RowCount = e.RowCount,
        Fingerprint = e.Fingerprint,
        SqlPreview = Preview(e.SqlText),
    };

    private static string Preview(string sql)
    {
        var folded = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return folded.Length > 200 ? $"{folded[..200]}…" : folded;
    }
}
