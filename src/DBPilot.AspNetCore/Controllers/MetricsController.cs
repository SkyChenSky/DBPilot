using DBPilot.Common;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>实例性能指标趋势（60s 采集 → 服务端分桶降采样 → 前端卡片图表）。</summary>
[Route("api")]
public class MetricsController(InstanceMetricsQueryService service) : ApiControllerBase
{
    /// <summary>指标趋势列式序列：from/to 必填，bucketSeconds 缺省按跨度自适应（≥7 天→5min、≥3 天→2min、其余→60s）。</summary>
    [HttpGet("instances/{id:int}/metrics/trend")]
    public async Task<IActionResult> Trend(int id, DateTime from, DateTime to, int? bucketSeconds)
    {
        var trend = await service.GetTrendAsync(id, from, to, bucketSeconds);
        return trend is null
            ? Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured))
            : Ok(ApiResponse.Ok(trend));
    }

    /// <summary>磁盘使用率趋势：范围内出现过的每卷一条序列（同样分桶降采样）。</summary>
    [HttpGet("instances/{id:int}/metrics/disk")]
    public async Task<IActionResult> Disk(int id, DateTime from, DateTime to)
    {
        var trend = await service.GetDiskTrendAsync(id, from, to);
        return trend is null
            ? Ok(ApiResponse.Fail(InstanceConfigResolver.DbNotConfigured))
            : Ok(ApiResponse.Ok(trend));
    }
}
