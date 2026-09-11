using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>慢 SQL 明细（dbpilot_slow_sql）。</summary>
[Table("dbpilot_slow_sql")]
public class DbpilotSlowSql
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }

    /// <summary>已按平台 UTC 校正</summary>
    [Column("event_time")] public DateTime EventTime { get; set; }

    [Column("db_name")] public string? DbName { get; set; }
    [Column("login_name")] public string? LoginName { get; set; }
    [Column("host_name")] public string? HostName { get; set; }
    [Column("app_name")] public string? AppName { get; set; }
    [Column("session_id")] public int? SessionId { get; set; }

    /// <summary>1=rpc 2=batch</summary>
    [Column("sql_type")] public int SqlType { get; set; }

    [Column("duration_ms")] public long DurationMs { get; set; }
    [Column("cpu_ms")] public long? CpuMs { get; set; }
    [Column("logical_reads")] public long? LogicalReads { get; set; }
    [Column("physical_reads")] public long? PhysicalReads { get; set; }
    [Column("writes")] public long? Writes { get; set; }
    [Column("row_count")] public long? RowCount { get; set; }
    [Column("fingerprint")] public string? Fingerprint { get; set; }
    [Column("sql_text")] public string SqlText { get; set; } = string.Empty;
    [Column("create_time")] public DateTime CreateTime { get; set; }
}
