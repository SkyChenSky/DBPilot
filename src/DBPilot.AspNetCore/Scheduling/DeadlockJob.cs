using DBPilot.Core.Deadlocks;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>死锁采集（默认 60s，DBPilot:Jobs:Deadlock；版本双路径见 DeadlockCollectService）。</summary>
[DisallowConcurrentExecution]
public class DeadlockJob(DeadlockCollectService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.CollectAllAsync(ct);
}
