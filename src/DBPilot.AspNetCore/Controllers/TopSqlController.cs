using DBPilot.Common;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.TopSql;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

/// <summary>
/// Top SQL：实时（DMV 累计快照）+ 历史（分钟差值聚合，TopSqlDeltaJob 采集）；
/// 执行计划分析（计划快照/变更检测，QueryPlanJob 每 5 分钟采集）。
/// 排除规则分层：文本NULL + RDS 官方标记（SQL 内固定）；巡检脚本特征（配置 DBPilot:TopSqlExcludePatterns）；
/// 指纹黑名单（平台库，/top-sql/exclusions 维护）；系统库兜底开关（页面“排除系统库”按钮，excludeSystemDb 参数）。
/// </summary>
[Route("api")]
public class TopSqlController(TopSqlService service, QueryPlanQueryService planService, TopSqlExcludeOptions excludes) : ApiControllerBase
{
    /// <summary>实时 Top SQL：实例启动以来累计快照（前端轮询 10s）。metric=avg|total，topN 默认 20；
    /// excludeSystemDb = 页面“排除系统库”开关（特征模式覆盖不全时一键降噪，“全部库”时额外排除 master 上下文行）。</summary>
    [HttpGet("instances/{id:int}/top-sql/realtime")]
    public async Task<IActionResult> Realtime(int id, string? db, string? metric, int? topN, bool excludeSystemDb = false)
    {
        var filter = new TopSqlFilter
        {
            ExcludeSystemDb = excludeSystemDb,
            Patterns = excludes.Patterns,
        };
        var result = await service.GetRealtimeAsync(id, db, metric, topN, filter);
        return Ok(result);
    }

    /// <summary>历史 Top SQL 总榜：保留期内全部差值按（指纹, 库）聚合的单一排行。
    /// metric=total|avg|count|cpu|reads（排序口径，默认 total），topN 默认 20；
    /// 噪音排除与采集同源（特征模式采集期过滤 + 指纹黑名单查询期过滤）。</summary>
    [HttpGet("instances/{id:int}/top-sql/history")]
    public IActionResult History(int id, string? db, string? metric, int? topN, bool excludeSystemDb = false)
    {
        var result = service.GetHistory(id, db, metric, topN, excludeSystemDb);
        return Ok(result);
    }

    /// <summary>指纹黑名单列表（已排除项，支持恢复）。</summary>
    [HttpGet("top-sql/exclusions")]
    public async Task<IActionResult> GetExclusions()
    {
        var result = await service.GetExclusionsAsync();
        return Ok(result);
    }

    public record ExcludeRequest(string Fingerprint, string? SqlHead);

    /// <summary>加入指纹黑名单（全局生效，query_hash 跨实例稳定）。</summary>
    [HttpPost("top-sql/exclusions")]
    public async Task<IActionResult> AddExclusion([FromBody] ExcludeRequest req)
    {
        var result = await service.AddExclusionAsync(req.Fingerprint, req.SqlHead);
        return Ok(result);
    }

    /// <summary>移出指纹黑名单（恢复显示）。</summary>
    [HttpDelete("top-sql/exclusions/{id:int}")]
    public async Task<IActionResult> RemoveExclusion(int id)
    {
        var result = await service.RemoveExclusionAsync(id);
        return Ok(result);
    }

    /// <summary>计划版本与变更（SQL 行"计划"入口弹窗）：该指纹下全部计划版本 + 变更时间线。
    /// 非 SQL Server 实例返回明确报错（引擎守卫）。</summary>
    [HttpGet("instances/{id:int}/top-sql/plans")]
    public async Task<IActionResult> Plans(int id, string fingerprint, string? db)
        => Ok(await planService.GetPlans(id, fingerprint, db));

    /// <summary>计划树（弹窗"计划树"tab）：planId 对应 XML 解析的 RelOp 递归折叠树。</summary>
    [HttpGet("instances/{id:int}/top-sql/plan-tree/{planId:long}")]
    public async Task<IActionResult> PlanTree(int id, long planId)
        => Ok(await planService.GetPlanTree(id, planId));

    /// <summary>计划 XML 原文（弹窗"XML"tab；驱逐未留存返回 null）。</summary>
    [HttpGet("instances/{id:int}/top-sql/plan-xml/{planId:long}")]
    public async Task<IActionResult> PlanXml(int id, long planId)
        => Ok(await planService.GetPlanXml(id, planId));

    /// <summary>最近计划变更榜（"计划变更"tab）：topN 默认 100，JOIN 模板表带 SQL 文本与前后均值倍数。</summary>
    [HttpGet("instances/{id:int}/top-sql/plan-changes")]
    public async Task<IActionResult> PlanChanges(int id, int? topN)
        => Ok(await planService.GetPlanChanges(id, topN));
}
