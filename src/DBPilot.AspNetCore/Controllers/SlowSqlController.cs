using DBPilot.Common;
using DBPilot.Core.Instances;
using DBPilot.Core.SlowSql;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 慢日志：列表 + 模板聚合 + 详情 + 趋势；采集属 SlowSqlJob。
/// </summary>
[Route("api")]
public class SlowSqlController(SlowSqlService service) : ApiControllerBase
{
    /// <summary>明细分页：from/to/db/minDurationMs/fingerprint/excludeSystemDb 筛选，时间倒序（默认近 1h，duration 降序口径见页面排序）。</summary>
    [HttpGet("instances/{id:int}/slow-sql")]
    public async Task<IActionResult> List(int id, [FromQuery] PageListParams pageParams,
        DateTime? from, DateTime? to, string? db, long? minDurationMs, string? fingerprint, bool excludeSystemDb = false)
    {
        var page = await service.GetPageAsync(id, from, to, db, minDurationMs, fingerprint, excludeSystemDb, pageParams.Page, pageParams.Limit);
        if (page is null)
            return Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured));

        return Ok(ApiResponse.Ok(page));
    }

    /// <summary>单条全文（样例展开）。</summary>
    [HttpGet("slow-sql/{rowId:int}")]
    public async Task<IActionResult> Detail(int rowId)
    {
        var item = await service.GetDetailAsync(rowId);
        return item is null
            ? Ok(ApiResponse.Fail($"慢SQL记录 {rowId} 不存在"))
            : Ok(ApiResponse.Ok(item));
    }

    /// <summary>模板聚合 Top（按总耗时降序）。</summary>
    [HttpGet("instances/{id:int}/slow-sql/templates")]
    public async Task<IActionResult> Templates(int id, DateTime? from, DateTime? to, string? db, long? minDurationMs, bool excludeSystemDb = false)
    {
        var list = await service.GetTemplatesAsync(id, from, to, db, minDurationMs, excludeSystemDb);
        if (list is null)
            return Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured));

        return Ok(ApiResponse.Ok(list));
    }

    /// <summary>量级趋势（单位时间条数与总耗时；粒度自适应；筛选口径与明细一致）。</summary>
    [HttpGet("instances/{id:int}/slow-sql/stats")]
    public async Task<IActionResult> Stats(int id, DateTime? from, DateTime? to, string? db, long? minDurationMs,
        string? fingerprint, bool excludeSystemDb = false)
    {
        var list = await service.GetTrendAsync(id, from, to, db, minDurationMs, fingerprint, excludeSystemDb);
        if (list is null)
            return Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured));

        return Ok(ApiResponse.Ok(list));
    }
}
