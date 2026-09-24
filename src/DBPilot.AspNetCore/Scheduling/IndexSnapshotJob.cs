using DBPilot.Core.Indexes;
using Quartz;
using Serilog;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>索引每日快照（默认每日 03:10，DBPilot:Jobs:IndexSnapshot）：
/// 夜间全量 = 串行先缺失索引后索引使用率（含碎片扫描），各自 try/catch 互不影响；追加式落库见 IndexDiagnoseService。
/// 手动触发 = 页面「重新采集」经 JobDataMap 传 instanceId/kind 单实例异步执行
/// （[DisallowConcurrentExecution] 天然防与夜间全量重叠；大库分钟级，接口立即返回不等待）。</summary>
[DisallowConcurrentExecution]
public class IndexSnapshotJob(IndexDiagnoseService service) : DbpilotJob
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var data = Context?.MergedJobDataMap;
        if (data is not null && data.ContainsKey("instanceId"))
        {
            await service.RunInstanceAsync((int)data["instanceId"], (string?)data["kind"] ?? "missing");
            return;
        }

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
