using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>计划变更事件（dbpilot_plan_change）：同指纹出现新 plan_hash 时一条，前后指标检测时固化。</summary>
[Table("dbpilot_plan_change")]
public class DbpilotPlanChange
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public long Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [Column("db_name")] public string? DbName { get; set; }

    [Column("old_plan_hash")] public string? OldPlanHash { get; set; }
    [Column("new_plan_hash")] public string NewPlanHash { get; set; } = string.Empty;

    /// <summary>老/新计划各自累计总量的均值（无老计划或除零为 NULL）。</summary>
    [Column("old_avg_elapsed_ms")] public long? OldAvgElapsedMs { get; set; }
    [Column("new_avg_elapsed_ms")] public long? NewAvgElapsedMs { get; set; }
    [Column("old_avg_worker_ms")] public long? OldAvgWorkerMs { get; set; }
    [Column("new_avg_worker_ms")] public long? NewAvgWorkerMs { get; set; }
    [Column("old_avg_reads")] public long? OldAvgReads { get; set; }
    [Column("new_avg_reads")] public long? NewAvgReads { get; set; }
    [Column("old_exec_count")] public long? OldExecCount { get; set; }
    [Column("new_exec_count")] public long? NewExecCount { get; set; }

    /// <summary>新计划 compile_time（兜底采集时间）。</summary>
    [Column("changed_at_utc")] public DateTime ChangedAtUtc { get; set; }

    [Column("create_time")] public DateTime CreateTime { get; set; }
}
