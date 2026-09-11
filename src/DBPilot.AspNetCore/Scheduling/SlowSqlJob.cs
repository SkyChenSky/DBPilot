using DBPilot.Core.SlowSql;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>慢SQL采集（默认 30s，DBPilot:Jobs:SlowSql；会话保障 + fn_xe 增量见 SlowSqlCollectService）。</summary>
[DisallowConcurrentExecution]
public class SlowSqlJob(SlowSqlCollectService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.CollectAllAsync(ct);
}
