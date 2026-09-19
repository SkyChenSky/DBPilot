using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>查询计划快照（dbpilot_query_plan）：每 (实例, 指纹, plan_hash) 一行；差量刷新——计数器前进（有真实执行）才更新行。</summary>
[Table("dbpilot_query_plan")]
public class DbpilotQueryPlan
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public long Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [Column("db_name")] public string? DbName { get; set; }
    [Column("query_plan_hash")] public string QueryPlanHash { get; set; } = string.Empty;

    /// <summary>首次发现新 plan_hash 时从缓存抓的 XML；超 1MB 存 NULL（驱逐后无从补抓）。</summary>
    [Column("plan_xml")] public string? PlanXml { get; set; }

    /// <summary>qs.creation_time，已按实例时区换算 UTC。</summary>
    [Column("compile_time_utc")] public DateTime? CompileTimeUtc { get; set; }

    [Column("first_seen_utc")] public DateTime FirstSeenUtc { get; set; }

    /// <summary>最后一次计数器前进（≈ 最后一次执行）时刻；休眠计划保持旧值不逐拍刷新。</summary>
    [Column("last_seen_utc")] public DateTime LastSeenUtc { get; set; }

    /// <summary>plan_handle hex —— 采集当拍 XML 抓取的寻址键（仅缓存存活期内有效，不落库）。</summary>
    [NotMapped]
    public string? PlanHandleHex { get; set; }

    /// <summary>语句在批文本中的字节偏移（text_query_plan 语句级寻址键，随 handle 一起透传，不落库）。</summary>
    [NotMapped]
    public int StatementStartOffset { get; set; }

    [NotMapped]
    public int StatementEndOffset { get; set; }

    /// <summary>该计划缓存累计（非差值），计数器前进时整体刷新。</summary>
    [Column("execution_count")] public long ExecutionCount { get; set; }
    [Column("total_elapsed_ms")] public long TotalElapsedMs { get; set; }
    [Column("total_worker_ms")] public long TotalWorkerMs { get; set; }
    [Column("total_logical_reads")] public long TotalLogicalReads { get; set; }

    [Column("create_time")] public DateTime CreateTime { get; set; }
}
