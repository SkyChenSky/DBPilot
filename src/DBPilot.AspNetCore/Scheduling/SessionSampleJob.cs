using DBPilot.Core.PerformanceInsight;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>会话采样（默认 10s，DBPilot:Jobs:SessionSample）。</summary>
[DisallowConcurrentExecution]
public class SessionSampleJob(SessionSamplingService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.SampleAllAsync(ct);
}

/// <summary>分钟聚合落库（默认 60s，DBPilot:Jobs:SampleFlush）。</summary>
[DisallowConcurrentExecution]
public class SampleFlushJob(SampleFlushService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.FlushAsync();
}
