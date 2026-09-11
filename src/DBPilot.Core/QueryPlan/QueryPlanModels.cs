namespace DBPilot.Core.QueryPlan;

/// <summary>
/// 计划快照采集原始行（Provider → Core）：dm_exec_query_stats 按 (指纹, plan_hash, plan_handle) 聚合，
/// 时间列为微秒累计（平台侧 /1000 转 ms），creation_time 已换算 UTC。同指纹同 plan_hash 多 handle
/// 行由采集服务 C# 侧合并取首。
/// </summary>
public class QueryPlanRawRow
{
    /// <summary>query_hash hex（与 top_sql_delta 同口径，NULL 兜底 sql_handle hex）。</summary>
    public string Fingerprint { get; set; } = "";

    public string? DbName { get; set; }

    /// <summary>query_plan_hash 16 位小写 hex。</summary>
    public string QueryPlanHash { get; set; } = "";

    /// <summary>plan_handle 小写 hex（XML 抓取的寻址键，仅缓存存活期内有效）。</summary>
    public string PlanHandleHex { get; set; } = "";

    /// <summary>语句在批文本中的字节偏移（text_query_plan 语句级寻址；-1 = 整批条目）。</summary>
    public int StatementStartOffset { get; set; }

    public int StatementEndOffset { get; set; }

    public DateTime? CreationTimeUtc { get; set; }
    public long ExecutionCount { get; set; }
    public long TotalElapsedUs { get; set; }
    public long TotalWorkerUs { get; set; }
    public long TotalLogicalReads { get; set; }
}

/// <summary>计划抓取寻址键：plan_handle + 语句偏移（dm_exec_text_query_plan 语句级寻址）。
/// Key 为返回字典的统一键（handle 小写归一），同 handle 多语句不撞键。</summary>
public record QueryPlanHandleRef(string HandleHex, int StartOffset, int EndOffset)
{
    public string Key => $"{HandleHex.ToLowerInvariant()}|{StartOffset}|{EndOffset}";
}

/// <summary>计划版本行（弹窗"版本列表"：该指纹下全部计划快照，HasXml 标记 XML 是否可看）。</summary>
public class PlanVersionItem
{
    public long PlanId { get; set; }
    public string QueryPlanHash { get; set; } = "";
    public DateTime? CompileTimeUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long ExecutionCount { get; set; }
    public long? AvgElapsedMs { get; set; }
    public long? AvgWorkerMs { get; set; }
    public long? AvgReads { get; set; }
    public bool HasXml { get; set; }
}

/// <summary>计划变更事件（弹窗"变更时间线"：前后指标为检测时固化的累计均值）。</summary>
public class PlanChangeItem
{
    public long Id { get; set; }
    public string? OldPlanHash { get; set; }
    public string NewPlanHash { get; set; } = "";
    public long? OldAvgElapsedMs { get; set; }
    public long? NewAvgElapsedMs { get; set; }
    public long? OldAvgWorkerMs { get; set; }
    public long? NewAvgWorkerMs { get; set; }
    public long? OldAvgReads { get; set; }
    public long? NewAvgReads { get; set; }
    public long? OldExecCount { get; set; }
    public long? NewExecCount { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}

/// <summary>计划弹窗聚合（版本列表 + 变更时间线）。</summary>
public class PlanVersionsResult
{
    public string Fingerprint { get; set; } = "";
    public string? DbName { get; set; }
    public List<PlanVersionItem> Items { get; set; } = [];
    public List<PlanChangeItem> Changes { get; set; } = [];
}

/// <summary>计划变更榜行（Top SQL 页"计划变更"tab：SQL 文本 JOIN 模板表，倍数为前端派生口径服务端算好）。</summary>
public class PlanChangeBoardItem : PlanChangeItem
{
    public int InstanceId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? DbName { get; set; }
    public string? SqlText { get; set; }

    /// <summary>new/old 均值倍数（old 为 0/NULL 置 NULL；>1 变慢、&lt;1 变快）。</summary>
    public double? ElapsedRatio { get; set; }
    public double? WorkerRatio { get; set; }
    public double? ReadsRatio { get; set; }
}
