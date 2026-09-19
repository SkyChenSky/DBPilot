using DBPilot.Common;
using DBPilot.Core.Blocking;
using DBPilot.Core.Instances;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 锁阻塞：实时阻塞树 + 历史事件（留痕/统计/趋势/详情）；事件留痕由会话采样底座（SessionSampleJob）产生。
/// </summary>
[Route("api")]
public class BlockingController(BlockingService service) : ApiControllerBase
{
    /// <summary>当前阻塞树（前端轮询 5s）：阻塞链数量 / 最长阻塞 / 涉及会话数 + 树森林。</summary>
    [HttpGet("instances/{id:int}/blocking/current")]
    public async Task<IActionResult> Current(int id)
    {
        var result = await service.GetRealtimeAsync(id);
        return Ok(result);
    }

    /// <summary>历史阻塞事件分页：from/to 按开始时间过滤，resolved=null 全部，dbName 省略 = 全部库。</summary>
    [HttpGet("instances/{id:int}/blocking/events")]
    public async Task<IActionResult> History(int id, [FromQuery] PageListParams pageParams,
        DateTime? from, DateTime? to, bool? resolved, string? dbName)
    {
        var page = await service.GetHistoryPageAsync(id, from, to, resolved, dbName, pageParams.Page, pageParams.Limit);
        return page is null
            ? Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured))
            : Ok(ApiResponse.Ok(page));
    }

    /// <summary>阻塞数量统计（历史 tab 顶部卡片）：近一天 / 近一周 / 近两周。</summary>
    [HttpGet("instances/{id:int}/blocking/stats")]
    public async Task<IActionResult> Stats(int id)
    {
        var stats = await service.GetStatsAsync(id);
        return stats is null
            ? Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured))
            : Ok(ApiResponse.Ok(stats));
    }

    /// <summary>阻塞趋势（历史 tab 趋势图）：from/to 按开始时间过滤，按时间桶聚合次数 + 等待秒累计。</summary>
    [HttpGet("instances/{id:int}/blocking/trend")]
    public async Task<IActionResult> Trend(int id, DateTime from, DateTime to)
    {
        var trend = await service.GetTrendAsync(id, from, to);
        return trend is null
            ? Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured))
            : Ok(ApiResponse.Ok(trend));
    }

    /// <summary>历史阻塞事件详情：元信息 + chain_tree 链路快照。</summary>
    [HttpGet("blocking/events/{eventId:int}")]
    public async Task<IActionResult> Detail(int eventId)
    {
        var result = await service.GetEventDetailAsync(eventId);
        return Ok(result);
    }
}
