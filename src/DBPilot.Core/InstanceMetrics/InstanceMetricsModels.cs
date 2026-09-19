using System.Collections.Concurrent;
using DBPilot.Common;

namespace DBPilot.Core.InstanceMetrics;

/// <summary>
/// 实例性能指标快照（Provider 一次采集的原值集合，趋势页用）：
/// CPU/内存/IO 为单行直查值；性能计数器为原始行集合（value/base 配对与瞬时/差值分类在平台侧纯函数完成，
/// 计数器查询不做 instance_name 过滤 —— Buffer cache hit ratio 等 base 行 instance 为空，SQL 端过滤会丢行）。
/// </summary>
public class InstanceMetricsSnapshot
{
    public string? ProductVersion { get; set; }

    /// <summary>CPU 利用率 %（RING_BUFFER_SCHEDULER_MONITOR 最新一条 SystemHealth 采样）。</summary>
    public decimal? CpuUsagePct { get; set; }

    public long? OsTotalMemoryKb { get; set; }
    public long? OsAvailableMemoryKb { get; set; }

    /// <summary>SQL Server 进程物理内存（dm_os_process_memory.physical_memory_in_use_kb）。</summary>
    public long? SqlMemoryKb { get; set; }

    // IO 累计（dm_io_virtual_file_stats 全实例 SUM，差值÷间隔得 IOPS/吞吐）
    public long? IoReads { get; set; }
    public long? IoWrites { get; set; }
    public long? IoBytesRead { get; set; }
    public long? IoBytesWritten { get; set; }

    /// <summary>性能计数器原始行（RTRIM 后名称）。</summary>
    public List<CounterRow> Counters { get; set; } = [];
}

/// <summary>dm_os_performance_counters 原始行（Provider SQL 已按 counter_name 白名单 + 对象过滤收窄）。</summary>
public class CounterRow
{
    public string? CounterName { get; set; }
    public string? ObjectName { get; set; }
    public string? InstanceName { get; set; }
    public long CntrValue { get; set; }
}

/// <summary>累计计数器值集合（差值源；对应 DMV 行缺失时为 null → 该指标本拍差值为 null）。</summary>
public sealed class MetricCounterValues
{
    public long? BatchRequests { get; init; }
    public long? Transactions { get; init; }
    public long? Logins { get; init; }
    public long? Compilations { get; init; }
    public long? Recompilations { get; init; }
    public long? FullScans { get; init; }
    public long? LazyWrites { get; init; }
    public long? Deadlocks { get; init; }
    public long? LockTimeouts { get; init; }
    public long? LockWaits { get; init; }
    public long? IoReads { get; init; }
    public long? IoWrites { get; init; }
    public long? IoBytesRead { get; init; }
    public long? IoBytesWritten { get; init; }
}

/// <summary>瞬时指标集合（gauge：无基线直接落库）。</summary>
public sealed class MetricGauges
{
    public int? Ple { get; init; }
    public decimal? BufferCacheHitRatioPct { get; init; }
    public int? UserConnections { get; init; }
    public int? BlockedProcesses { get; init; }
}

/// <summary>磁盘卷用量原始行（dm_os_volume_stats，2008 无此 DMV → 空列表自然降级）。</summary>
public class InstanceDiskRawRow
{
    public string? VolumeMountPoint { get; set; }
    public long? TotalMb { get; set; }
    public long? AvailableMb { get; set; }
}

/// <summary>分钟差值基线（进程内存态；Host 重启或实例重启后重建，只建基线不落率值）。</summary>
public class InstanceMetricsBaseline
{
    /// <summary>false = 尚未建立基线（首扫/重启重置后），本拍只采样率值不落。</summary>
    public bool HasBaseline { get; set; }

    /// <summary>上次采样时刻（UTC，差值窗口的分母）。</summary>
    public DateTime SampleTimeUtc { get; set; }

    /// <summary>上次采样的累计计数器原值。</summary>
    public MetricCounterValues? Values { get; set; }
}

/// <summary>基线存储（实例 → 基线）。</summary>
public class InstanceMetricsBaselineStore : ISingletonDepend
{
    private readonly ConcurrentDictionary<int, InstanceMetricsBaseline> _map = new();

    public InstanceMetricsBaseline Of(int instanceId) => _map.GetOrAdd(instanceId, _ => new());
}
