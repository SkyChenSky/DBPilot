namespace DBPilot.Core.Instances;

/// <summary>
/// 采集账号所需的服务器级权限清单：缺失项逐条给出影响与修复脚本。
/// </summary>
public static class PermissionCatalog
{
    public class Required
    {
        public required string Permission { get; init; }
        public required string Impact { get; init; }
        public required string FixScript { get; init; }
    }

    /// <summary>慢 SQL XE 通道依赖 ALTER ANY EVENT SESSION；Kill 会话可选权限不在此列。</summary>
    public static readonly IReadOnlyList<Required> All =
    [
        new()
        {
            Permission = "VIEW SERVER STATE",
            Impact = "无法读取 DMV（性能洞察 / Top SQL / 索引诊断 / 阻塞 / 死锁全部不可用）",
            FixScript = "GRANT VIEW SERVER STATE TO <登录名>;"
        },
        new()
        {
            Permission = "VIEW ANY DATABASE",
            Impact = "无法跨库元数据查询（库列表 / 索引诊断按库筛选不可用）",
            FixScript = "GRANT VIEW ANY DATABASE TO <登录名>;"
        },
        new()
        {
            // The provider checks permissions at SERVER scope. VIEW DEFINITION is
            // a database-level permission; its server-level equivalent is VIEW ANY DEFINITION.
            Permission = "VIEW ANY DEFINITION",
            Impact = "无法读取索引 / 外键元数据（缺失索引建议、未使用索引标记不可用）",
            FixScript = "USE [master]; GRANT VIEW ANY DEFINITION TO <登录名>;"
        },
        new()
        {
            Permission = "ALTER ANY EVENT SESSION",
            Impact = "无法创建 / 启动慢 SQL 捕获 XE 会话（慢日志功能不可用）",
            FixScript = "GRANT ALTER ANY EVENT SESSION TO <登录名>;"
        }
    ];

    /// <summary>对照实例返回的有效权限集，输出缺失项（含影响与修复脚本）。</summary>
    public static List<MissingPermission> FindMissing(IEnumerable<string> grantedPermissions)
    {
        var granted = new HashSet<string>(grantedPermissions, StringComparer.OrdinalIgnoreCase);
        return [.. All
            .Where(r => !granted.Contains(r.Permission))
            .Select(r => new MissingPermission
            {
                Permission = r.Permission,
                Impact = r.Impact,
                FixScript = r.FixScript
            })];
    }
}
