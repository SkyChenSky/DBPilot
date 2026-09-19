using DBPilot.Core.QueryPlan;
using Quartz;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>计划快照采集（默认 5 分钟，DBPilot:Jobs:QueryPlan；变更检测与种子规则见 QueryPlanCollectService）。</summary>
[DisallowConcurrentExecution]
public class QueryPlanJob(QueryPlanCollectService service) : DbpilotJob
{
    protected override Task RunAsync(CancellationToken ct) => service.CollectAllAsync(ct);
}
