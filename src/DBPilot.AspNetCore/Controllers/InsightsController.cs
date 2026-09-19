using DBPilot.Common;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.TopSql;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// 性能洞察：AAS 趋势 + 七维 / Load By SQL。
/// Load By SQL 排除规则对齐 Top SQL：RDS/平台标记 + 配置 DBPilot:TopSqlExcludePatterns + 指纹黑名单（服务内加载）。
/// </summary>
[Route("api")]
public class InsightsController(PerformanceInsightService service, TopSqlExcludeOptions excludes) : ApiControllerBase
{
    /// <summary>实时 AAS（内存环形缓冲直出，10s 粒度；minutes ≤ 60 默认 15，前端轮询 10s）。</summary>
    [HttpGet("instances/{id:int}/insights/aas/realtime")]
    public async Task<IActionResult> Realtime(int id, [FromQuery] int minutes = 15)
    {
        var result = await service.GetRealtimeAsync(id, minutes);
        return Ok(result);
    }

    /// <summary>历史 AAS（分钟聚合表；start/end 为 UTC ISO 8601，默认近 24h，跨 ≤ 7 天）。</summary>
    [HttpGet("instances/{id:int}/insights/aas")]
    public async Task<IActionResult> History(int id, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var result = await service.GetHistoryAsync(id, start, end);
        return Ok(result);
    }

    /// <summary>Load By SQL（区间 ≤5min 走内存缓冲，否则分钟表；Top10 AAS 贡献 + 占比 + 语句文本）。</summary>
    [HttpGet("instances/{id:int}/insights/aas/top-sql")]
    public async Task<IActionResult> TopSql(int id, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var patterns = excludes.Patterns;
        var result = await service.GetTopSqlAsync(id, start, end, patterns);
        return Ok(result);
    }

    /// <summary>单条 SQL 的 AAS 趋势（Load By SQL 行点击查看；fingerprint 指纹，start/end 同上）。</summary>
    [HttpGet("instances/{id:int}/insights/aas/sql-trend")]
    public async Task<IActionResult> SqlTrend(int id, [FromQuery] string fingerprint, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var result = await service.GetSqlTrendAsync(id, fingerprint ?? "", start, end);
        return Ok(result);
    }
}
