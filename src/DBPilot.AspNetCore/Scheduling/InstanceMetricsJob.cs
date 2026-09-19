using DBPilot.Core.InstanceMetrics;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>实例性能指标采集（默认 10s，DBPilot:Jobs:InstanceMetrics；模块开关见 InstanceMetricsExtension）。</summary>
[DisallowConcurrentExecution]
public class InstanceMetricsJob(InstanceMetricsCollectService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.CollectAllAsync(ct);
}
