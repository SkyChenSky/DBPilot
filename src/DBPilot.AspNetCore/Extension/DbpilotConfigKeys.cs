namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 配置树键名单一来源（DBPilot 单树）：代码中读取配置一律引用本类常量，
/// 与 appsettings.template.json / README 配置表对应；新增配置项在此登记。
/// </summary>
public static class DbpilotConfigKeys
{
    /// <summary>平台库连接串（空 = 平台库未配置，各功能走降级分支）。</summary>
    public const string ConnectionString = "DBPilot:ConnectionString";

    /// <summary>平台库引擎（sqlserver / mysql / postgresql / sqlite，无默认值；AddDBPilot 自动读取——委托枚举 PlatformEngine 优先）。</summary>
    public const string PlatformEngine = "DBPilot:PlatformEngine";

    /// <summary>启动时自动初始化平台库结构（AddDBPilot 自动读取做底；委托 o.InitSchema = false 覆盖）。</summary>
    public const string AutoInitSchema = "DBPilot:AutoInitSchema";

    /// <summary>进程角色开关（Web/Collector；AddDBPilot 自动读取做底，委托覆盖——预编译产物切 1+N 拓扑的入口）。</summary>
    public const string Roles = "DBPilot:Roles";

    /// <summary>Quartz Job cron 覆盖（Job 名 → cron；null/空串 = 禁用该 Job）。</summary>
    public const string Jobs = "DBPilot:Jobs";

    /// <summary>登录认证（Username/PasswordHash/Secret）。</summary>
    public const string Auth = "DBPilot:Auth";

    /// <summary>采集行为（退避阈值等）。</summary>
    public const string Collect = "DBPilot:Collect";

    /// <summary>数据保留天数（Housekeeping）。</summary>
    public const string Retention = "DBPilot:Retention";

    /// <summary>TopSQL 噪音排除 LIKE 模式列表。</summary>
    public const string TopSqlExcludePatterns = "DBPilot:TopSqlExcludePatterns";

    /// <summary>MCP 出口（ApiKey 非空即开启 + MaxSqlHeadLength/MaxRows；无独立开关）。</summary>
    public const string Mcp = "DBPilot:Mcp";
}
