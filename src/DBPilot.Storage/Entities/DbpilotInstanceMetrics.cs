using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>实例性能指标分钟快照（dbpilot_instance_metrics：瞬时列直接填、率列 = 累计差值÷间隔秒）。</summary>
[Table("dbpilot_instance_metrics")]
public class DbpilotInstanceMetrics
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public long Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }

    [Column("sample_time")] public DateTime SampleTime { get; set; }

    /// <summary>CPU 利用率 %（ring buffer 最新 SystemHealth 采样）。</summary>
    [Column("cpu_usage_pct")] public decimal? CpuUsagePct { get; set; }

    /// <summary>OS 内存使用率 %（(总-可用)/总）。</summary>
    [Column("mem_usage_pct")] public decimal? MemUsagePct { get; set; }

    [Column("os_total_memory_kb")] public long? OsTotalMemoryKb { get; set; }
    [Column("os_available_memory_kb")] public long? OsAvailableMemoryKb { get; set; }
    [Column("sql_memory_kb")] public long? SqlMemoryKb { get; set; }

    [Column("qps")] public decimal? Qps { get; set; }
    [Column("tps")] public decimal? Tps { get; set; }
    [Column("logins_sec")] public decimal? LoginsPerSec { get; set; }
    [Column("compilations_sec")] public decimal? CompilationsPerSec { get; set; }
    [Column("recompilations_sec")] public decimal? RecompilationsPerSec { get; set; }
    [Column("full_scans_sec")] public decimal? FullScansPerSec { get; set; }
    [Column("lazy_writes_sec")] public decimal? LazyWritesPerSec { get; set; }

    /// <summary>Page life expectancy（秒，瞬时）。</summary>
    [Column("ple")] public int? Ple { get; set; }

    /// <summary>缓冲命中率 %（value/base×100，瞬时）。</summary>
    [Column("buffer_cache_hit_ratio_pct")] public decimal? BufferCacheHitRatioPct { get; set; }

    [Column("deadlocks_sec")] public decimal? DeadlocksPerSec { get; set; }
    [Column("lock_timeouts_sec")] public decimal? LockTimeoutsPerSec { get; set; }
    [Column("lock_waits_sec")] public decimal? LockWaitsPerSec { get; set; }
    [Column("user_connections")] public int? UserConnections { get; set; }
    [Column("blocked_processes")] public int? BlockedProcesses { get; set; }

    [Column("iops_read")] public decimal? IopsRead { get; set; }
    [Column("iops_write")] public decimal? IopsWrite { get; set; }
    [Column("mbps_read")] public decimal? MbpsRead { get; set; }
    [Column("mbps_write")] public decimal? MbpsWrite { get; set; }
}
