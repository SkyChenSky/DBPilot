using DBPilot.Core.TopSql;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>Top SQL 分钟差值采集（默认 60s，DBPilot:Jobs:TopSqlDelta；差值规则见 TopSqlDeltaCollectService）。</summary>
[DisallowConcurrentExecution]
public class TopSqlDeltaJob(TopSqlDeltaCollectService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.CollectAllAsync(ct);
}
