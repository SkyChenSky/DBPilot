using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>Top SQL 分钟差值（dbpilot_top_sql_delta）。</summary>
[Table("dbpilot_top_sql_delta")]
public class DbpilotTopSqlDelta
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;

    /// <summary>计划编译库上下文（ad hoc 动态 SQL 可能为 NULL）。</summary>
    [Column("db_name")] public string? DbName { get; set; }

    [Column("window_start")] public DateTime WindowStart { get; set; }
    [Column("window_end")] public DateTime WindowEnd { get; set; }
    [Column("exec_count")] public long ExecCount { get; set; }
    [Column("total_worker_ms")] public long TotalWorkerMs { get; set; }
    [Column("total_elapsed_ms")] public long TotalElapsedMs { get; set; }
    [Column("total_logical_reads")] public long TotalLogicalReads { get; set; }
    [Column("total_physical_reads")] public long TotalPhysicalReads { get; set; }
    [Column("total_writes")] public long TotalWrites { get; set; }
    [Column("max_elapsed_ms")] public long MaxElapsedMs { get; set; }
    [Column("create_time")] public DateTime CreateTime { get; set; }
}
