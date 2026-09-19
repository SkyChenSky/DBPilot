using System.Collections.Concurrent;
using DBPilot.Common;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>采集配置（appsettings Collect 节）：并发度 + 失败退避 [连续失败阈值, 退避秒数]。</summary>
public class CollectOptions
{
    public int MaxDegreeOfParallelism { get; set; } = 8;

    /// <summary>[0] = 连续失败多少次进入退避（默认 5）；[1] = 退避时长秒（默认 30）。</summary>
    public int[] FailureBackoff { get; set; } = [5, 30];

    public int FailureThreshold => FailureBackoff is { Length: > 0 } ? Math.Max(1, FailureBackoff[0]) : 5;
    public int BackoffSeconds => FailureBackoff is { Length: > 1 } ? Math.Max(1, FailureBackoff[1]) : 30;
}

/// <summary>实例采集状态（退避用；进程内存态，重启即清零 —— 可接受）。
/// 键为 (实例, 采集器)：多个采集 Job 共用本存储，只按实例键会让高频采集器（如 10s 采样）的成功
/// 清零低频采集器（分钟级）的连续失败计数 → 永远到不了告警阈值=静默失败。</summary>
public class CollectStateStore : ISingletonDepend
{
    private readonly ConcurrentDictionary<(int InstanceId, string Collector), InstanceCollectState> _map = new();

    /// <summary>collector 为采集器标签（与传给 CollectRunner.ForEachEnabledAsync 的 label 一致）。</summary>
    public InstanceCollectState Of(int instanceId, string collector)
        => _map.GetOrAdd((instanceId, collector), _ => new());
}

public class InstanceCollectState
{
    private int _consecutiveFailures;

    /// <summary>退避截止时刻（UTC）；在退避中的采样直接跳过。</summary>
    public DateTime? SkipUntilUtc { get; private set; }

    public bool InBackoff => SkipUntilUtc > DateTime.UtcNow;

    public void OnSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        SkipUntilUtc = null;
    }

    /// <summary>失败计数 +1；连续失败达阈值时进入退避并返回 true（调用方记一条告警日志即可）。</summary>
    public bool OnFailure(int threshold, int backoffSeconds)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= threshold)
        {
            SkipUntilUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return true;
        }
        return false;
    }
}
