using DBPilot.Common;
using DBPilot.Core.Indexes;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 索引诊断（缺失索引 + 索引使用率，碎片并入使用率快照）：
/// 缺失索引与索引使用率均为快照单一口径，由 IndexSnapshotJob 每日采集 + 页面「重新采集」立即补采。
/// </summary>
[Route("api")]
public class IndexController(IndexDiagnoseService service) : ApiControllerBase
{
    /// <summary>缺失索引重新采集（页面按钮）：对该实例立即跑一次快照采集（与每日 03:10 Job 同一代码路径）。</summary>
    [HttpPost("instances/{id:int}/index/missing/recollect")]
    public async Task<IActionResult> MissingRecollect(int id)
    {
        var result = await service.RecollectAsync(id);
        return Ok(result);
    }

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

    /// <summary>索引使用率重新采集（页面按钮）：DMV 使用率 + 碎片扫描并入同一快照（大库分钟级，前端超时已放宽）。</summary>
    [HttpPost("instances/{id:int}/index/usage/recollect")]
    public async Task<IActionResult> UsageRecollect(int id)
    {
        var result = await service.RecollectUsageAsync(id);
        return Ok(result);
    }

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
}
