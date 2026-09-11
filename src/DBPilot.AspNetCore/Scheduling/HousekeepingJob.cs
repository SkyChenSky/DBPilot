using DBPilot.Core.Housekeeping;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>历史数据清理（默认每日 02:30，DBPilot:Jobs:Housekeeping；分批删除见 HousekeepingService）。</summary>
[DisallowConcurrentExecution]
public class HousekeepingJob(HousekeepingService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.RunAsync(ct);
}
