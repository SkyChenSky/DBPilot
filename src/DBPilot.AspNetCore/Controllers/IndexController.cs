using DBPilot.Common;
using DBPilot.Core.Indexes;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 索引诊断（缺失索引 + 索引使用率，碎片并入使用率快照）：
/// 缺失索引与索引使用率均为快照单一口径，由 IndexSnapshotJob 每日采集 + 页面「重新采集」异步补采。
/// 手动采集触发 IndexSnapshotJob 单实例执行（接口立即返回，大库分钟级由后台完成，
/// 页面以"上次采集"时间戳感知完成；5 分钟冷却防连点）。
/// </summary>
[Route("api")]
public class IndexController(IndexDiagnoseService service, ISchedulerFactory schedulerFactory) : ApiControllerBase
{
    /// <summary>缺失索引重新采集（页面按钮）：冷却闸门 + 触发 Job 异步执行。</summary>
    [HttpPost("instances/{id:int}/index/missing/recollect")]
    public async Task<IActionResult> MissingRecollect(int id)
        => Ok(await TriggerRecollectAsync(id, "missing"));

    /// <summary>最新缺失索引快照（每日 03:10 采集）：上次采集时间 + 近 7 天新增标记。</summary>
    [HttpGet("instances/{id:int}/index/missing/snapshots")]
    public async Task<IActionResult> MissingSnapshots(int id, string? db, bool excludeSystemDb = false)
    {
        var result = await service.GetMissingSnapshotAsync(id, db, excludeSystemDb);
        return Ok(result);
    }

    /// <summary>缺失索引变化趋势（按快照批次计数，时间升序；db 为空 = 全部库）。</summary>
    [HttpGet("instances/{id:int}/index/missing/trend")]
    public async Task<IActionResult> MissingTrend(int id, string? db, bool excludeSystemDb = false)
    {
        var result = await service.GetMissingTrendAsync(id, db, excludeSystemDb);
        return Ok(result);
    }

    /// <summary>索引使用率重新采集（页面按钮）：冷却闸门 + 触发 Job 异步执行（大库分钟级）。</summary>
    [HttpPost("instances/{id:int}/index/usage/recollect")]
    public async Task<IActionResult> UsageRecollect(int id)
        => Ok(await TriggerRecollectAsync(id, "usage"));

    /// <summary>最新索引使用率快照（db 为空 = 全部库）：IsUnused 固化 + 碎片率 + 总览统计 + 运行时长警示。</summary>
    [HttpGet("instances/{id:int}/index/usage/snapshots")]
    public async Task<IActionResult> UsageSnapshots(int id, string? db, bool excludeSystemDb = false)
    {
        var result = await service.GetUsageSnapshotAsync(id, db, excludeSystemDb);
        return Ok(result);
    }

    /// <summary>索引空间变化趋势（每快照批次 used_page_count 求和；db 为空 = 全部库）。</summary>
    [HttpGet("instances/{id:int}/index/usage/trend")]
    public async Task<IActionResult> UsageTrend(int id, string? db, bool excludeSystemDb = false)
    {
        var result = await service.GetUsageTrendAsync(id, db, excludeSystemDb);
        return Ok(result);
    }

    /// <summary>触发闸门（冷却检查 + 占用）→ Quartz 立即执行单实例采集；JobKey 与 QuartzExtension 注册键同名。</summary>
    private async Task<ServiceResult<bool>> TriggerRecollectAsync(int id, string kind)
    {
        var gate = await service.TryBeginRecollectAsync(id, usage: kind == "usage");
        if (!gate.IsSuccess)
            return gate;

        // IScheduler 经工厂获取（Quartz 注册的是 ISchedulerFactory；WebOnly 形态调度器未启动时此处抛错提示明确）
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(new JobKey("IndexSnapshot"), new JobDataMap
        {
            ["instanceId"] = id,
            ["kind"] = kind,
        });
        return ServiceResult<bool>.Succeeded(true);
    }
}
