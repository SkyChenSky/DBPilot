using DBPilot.Core.Indexes;
using Quartz;
using Serilog;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>索引每日快照（默认每日 03:10，DBPilot:Jobs:IndexSnapshot）：
/// 串行先缺失索引后索引使用率（含碎片扫描），各自 try/catch 互不影响；追加式落库见 IndexDiagnoseService。</summary>
[DisallowConcurrentExecution]
public class IndexSnapshotJob(IndexDiagnoseService service) : DbpilotJob
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await service.CollectMissingSnapshotsAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "缺失索引快照采集失败（IndexSnapshotJob）");
        }

        try
        {
            await service.CollectUsageSnapshotsAsync(ct);
        }
        catch (Exception ex)
        {
            // 一个失败不影响另一个（如 ct 取消时记录后收尾）
            Log.Error(ex, "索引使用率快照采集失败（IndexSnapshotJob）");
        }
    }
}
