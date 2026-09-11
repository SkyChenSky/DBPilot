using DBPilot.Common;
using DBPilot.Core.Deadlocks;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 死锁：列表 + 详情/关系图数据；采集属 DeadlockJob。
/// </summary>
[Route("api")]
public class DeadlockController(DeadlockService service) : ApiControllerBase
{
    /// <summary>死锁分页列表：from/to 按 event_time 过滤，倒序；可选过滤：指纹/锁类型/涉及对象/登录名/主机名。
    /// 非 SQL Server 实例返回明确报错（引擎守卫）。</summary>
    [HttpGet("instances/{id:int}/deadlocks")]
    public async Task<IActionResult> List(int id, [FromQuery] PageListParams pageParams, DateTime? from, DateTime? to,
        string? fingerprint, string? lockType, string? objectName, string? loginName, string? hostName)
    {
        var result = await service.GetPageAsync(id, from, to, pageParams.Page, pageParams.Limit,
            fingerprint, lockType, objectName, loginName, hostName);
        return Ok(result);
    }

    /// <summary>列表过滤下拉选项（登录名/主机名，进程维度 distinct，时间窗与列表一致）。</summary>
    [HttpGet("instances/{id:int}/deadlocks/filters")]
    public async Task<IActionResult> FilterOptions(int id, DateTime? from, DateTime? to)
        => Ok(await service.GetFilterOptionsAsync(id, from, to));

    /// <summary>锁类型趋势（KEY/OBJECT/PAGE/RID/其他 分色；分色=事件×类型各计一次，Total=按事件计；粒度自适应）。</summary>
    [HttpGet("instances/{id:int}/deadlocks/stats")]
    public async Task<IActionResult> Stats(int id, DateTime? from, DateTime? to)
        => Ok(await service.GetTrendAsync(id, from, to));

    /// <summary>死锁速率趋势（降级形态）：无事件明细数据源的引擎读指标序列聚合，全引擎可用。</summary>
    [HttpGet("instances/{id:int}/deadlocks/stats-metrics")]
    public async Task<IActionResult> StatsMetrics(int id, DateTime? from, DateTime? to)
        => Ok(await service.GetMetricsTrendAsync(id, from, to));

    /// <summary>相似死锁归并 TOP（指纹 GROUP BY，同构死锁反复发生合并计数；摘要取最近一次事件）。</summary>
    [HttpGet("instances/{id:int}/deadlocks/fingerprints")]
    public async Task<IActionResult> Fingerprints(int id, DateTime? from, DateTime? to)
        => Ok(await service.GetFingerprintStatsAsync(id, from, to));

    /// <summary>死锁详情：进程/资源结构化（关系图）+ 辅助信息 + 原始 XML。</summary>
    [HttpGet("deadlocks/{eventId:int}")]
    public async Task<IActionResult> Detail(int eventId)
    {
        var result = await service.GetDetailAsync(eventId);
        return Ok(result);
    }
}
