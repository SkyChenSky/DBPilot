using Chloe;
using DBPilot.Common;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.QueryPlan;

/// <summary>
/// 计划快照查询（TopSqlController 计划弹窗 / 计划变更 tab）：
/// 版本列表与变更时间线（SqlQuery 端算均值 + HasXml，避免拉 plan_xml 大列；方言语句走 IPlatformDialect）；
/// 计划树 / XML 原文请求时按 planId 单行取 LINQ（PlanTreeBuilder 解析，不落库）。
/// </summary>
public class QueryPlanQueryService(IServiceProvider sp, IPlatformDialect dialect, ProviderRegistry registry) : IDepend
{
    private const int ChangeTopNCeiling = 500;

    /// <summary>指纹维度：该指纹下全部计划版本 + 变更事件（db 空为不过滤）。
    /// 非 SQL Server 实例返回明确报错（引擎守卫——计划快照源自 dm_exec_query_stats）。</summary>
    public async Task<ServiceResult<PlanVersionsResult>> GetPlans(int instanceId, string fingerprint, string? db)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<PlanVersionsResult>.Failed(InstanceConfigResolver.DbNotConfigured);
        if (fingerprint.IsNullOrWhiteSpace())
            return ServiceResult<PlanVersionsResult>.Failed("fingerprint 不能为空");

        var reject = await InstanceConfigResolver.RejectUnsupportedAsync(dbc, registry, instanceId, DbpilotFeatures.QueryPlanSnapshot);
        if (reject != null) return ServiceResult<PlanVersionsResult>.Failed(reject);

        try
        {
            var items = dbc.SqlQuery<PlanVersionItem>(dialect.PlanVersionsSql(),
                new { instanceId, fingerprint, db = db ?? "" });

            var changes = dbc.SqlQuery<PlanChangeItem>(dialect.PlanChangesTopSql(),
                new { instanceId, fingerprint });

            // 展示库上下文：最近一条带 db_name 的版本（轻量投影 + 内存排序——同指纹版本数个位数，
            // 且 Chloe LINQ 的 OrderBy 与 .NET 10 AsyncEnumerable 扩展撞名编译不过）
            var dbName = dbc.Query<DbpilotQueryPlan>()
                .Where(x => x.InstanceId == instanceId && x.Fingerprint == fingerprint && x.DbName != null)
                .Select(x => new { x.DbName, x.LastSeenUtc })
                .ToList()
                .OrderByDescending(x => x.LastSeenUtc)
                .FirstOrDefault()?.DbName;

            return ServiceResult<PlanVersionsResult>.Succeeded(new PlanVersionsResult
            {
                Fingerprint = fingerprint,
                DbName = dbName,
                Items = items,
                Changes = changes,
            });
        }
        catch (Exception ex)
        {
            return ServiceResult<PlanVersionsResult>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>计划树：planId 对应 XML 的 RelOp 递归结构（XML 缺失返回空列表）。</summary>
    public async Task<ServiceResult<List<PlanTreeNode>>> GetPlanTree(int instanceId, long planId)
    {
        var result = await GetPlanXml(instanceId, planId);
        return result.IsSuccess
            ? ServiceResult<List<PlanTreeNode>>.Succeeded(PlanTreeBuilder.Build(result.Data))
            : ServiceResult<List<PlanTreeNode>>.Failed(result.Message);
    }

    /// <summary>计划 XML 原文（缺失返回 null，前端展示"XML 未留存"；单行单列 LINQ，双方言免翻译）。
    /// 非 SQL Server 实例返回明确报错（引擎守卫）。</summary>
    public async Task<ServiceResult<string?>> GetPlanXml(int instanceId, long planId)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<string?>.Failed(InstanceConfigResolver.DbNotConfigured);

        var reject = await InstanceConfigResolver.RejectUnsupportedAsync(dbc, registry, instanceId, DbpilotFeatures.QueryPlanSnapshot);
        if (reject != null) return ServiceResult<string?>.Failed(reject);

        try
        {
            var xml = dbc.Query<DbpilotQueryPlan>()
                .Where(x => x.InstanceId == instanceId && x.Id == planId)
                .Select(x => x.PlanXml)
                .FirstOrDefault();
            return ServiceResult<string?>.Succeeded(xml);
        }
        catch (Exception ex)
        {
            return ServiceResult<string?>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>最近计划变更榜（JOIN dbpilot_sql_template 取 SQL 文本；倍数 new/old，old 为 0/NULL 置 NULL）。
    /// 非 SQL Server 实例返回明确报错（引擎守卫）。</summary>
    public async Task<ServiceResult<List<PlanChangeBoardItem>>> GetPlanChanges(int instanceId, int? topN)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<List<PlanChangeBoardItem>>.Failed(InstanceConfigResolver.DbNotConfigured);

        var reject = await InstanceConfigResolver.RejectUnsupportedAsync(dbc, registry, instanceId, DbpilotFeatures.QueryPlanSnapshot);
        if (reject != null) return ServiceResult<List<PlanChangeBoardItem>>.Failed(reject);

        var n = Math.Clamp(topN ?? 100, 1, ChangeTopNCeiling);

        try
        {
            var rows = dbc.SqlQuery<PlanChangeBoardItem>(dialect.PlanChangeBoardSql(n), new { instanceId }).ToList();

            foreach (var r in rows)
            {
                r.ElapsedRatio = Ratio(r.NewAvgElapsedMs, r.OldAvgElapsedMs);
                r.WorkerRatio = Ratio(r.NewAvgWorkerMs, r.OldAvgWorkerMs);
                r.ReadsRatio = Ratio(r.NewAvgReads, r.OldAvgReads);
            }

            return ServiceResult<List<PlanChangeBoardItem>>.Succeeded(rows);
        }
        catch (Exception ex)
        {
            return ServiceResult<List<PlanChangeBoardItem>>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>new/old 倍数（old 为 0/NULL、new 为 NULL 置 NULL）。</summary>
    internal static double? Ratio(long? newValue, long? oldValue)
        => newValue != null && oldValue > 0 ? Math.Round(newValue.Value * 1.0 / oldValue.Value, 2) : null;
}
