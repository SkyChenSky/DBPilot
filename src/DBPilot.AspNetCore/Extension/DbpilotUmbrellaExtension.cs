using DBPilot.Core.Providers;
using DBPilot.AspNetCore.Scheduling;
using Microsoft.Extensions.Configuration;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 全家桶组合选项：行为开关与平台库引擎以代码委托为最终口径；
/// Web/Collector/InitSchema 支持 DBPilot:Roles / DBPilot:AutoInitSchema 配置做底（AddDBPilot 自动读取，
/// 预编译形态切拓扑的入口），委托显式赋值覆盖配置。两个轴：Web/Collector 是进程形态（多进程部署时各自关一个）；
/// Mcp/InstanceMetrics 是模块级附加项。
/// </summary>
public sealed class DbpilotOptions
{
    /// <summary>
    /// 平台库引擎（无默认值，必须显式指定——null 回落 DBPilot:PlatformEngine 配置（本方法自动读取），
    /// 两者皆空启动期即抛）。取值枚举 DbpilotEngine（SqlServer / MySql / Sqlite——Sqlite 仅平台库轴）。
    /// </summary>
    public DbpilotEngine? PlatformEngine { get; set; }

    /// <summary>Web 出口（API/前端/登录/MCP）；false = 纯采集器进程。</summary>
    public bool Web { get; set; } = true;

    /// <summary>采集调度（9 个 Job + Housekeeping）；false = 纯 Web 进程（1 采集器 + N Web 拓扑用）。</summary>
    public bool Collector { get; set; } = true;

    /// <summary>MCP 出口模块（仅 Web=true 生效；还需配置 DBPilot:Mcp:ApiKey 才真正注册——安全默认关）。</summary>
    public bool Mcp { get; set; } = true;

    /// <summary>实例性能指标采集模块（仅 Collector=true 生效；单 Job 关停仍走 DBPilot:Jobs 置 null）。</summary>
    public bool InstanceMetrics { get; set; } = true;

    /// <summary>UseDBPilot 时先幂等初始化平台库结构（表结构归 DBA 管理的部署置 false）。</summary>
    public bool InitSchema { get; set; } = true;

    /// <summary>便捷预设：纯 Web 进程（不采集）。</summary>
    public DbpilotOptions WebOnly() { Collector = false; return this; }

    /// <summary>便捷预设：纯采集器进程（无 API/前端，全 404）。</summary>
    public DbpilotOptions CollectorOnly() { Web = false; return this; }

    /// <summary>
    /// 从配置读默认开关（Roles:Web/Collector + AutoInitSchema；键缺失 = true）。
    /// 预编译形态切拓扑的入口——AddDBPilot 以此做底，委托显式赋值覆盖。
    /// </summary>
    internal static DbpilotOptions ReadDefaults(IConfiguration configuration)
    {
        var roles = configuration.GetSection(DbpilotConfigKeys.Roles);
        return new DbpilotOptions
        {
            Web = roles.GetValue<bool?>("Web") ?? true,
            Collector = roles.GetValue<bool?>("Collector") ?? true,
            InitSchema = configuration.GetSection(DbpilotConfigKeys.AutoInitSchema).Get<bool?>() ?? true,
        };
    }
}

/// <summary>
/// 全家桶组合入口：一条链拉起全部模块，行为开关与平台库引擎经
/// <see cref="DbpilotOptions"/> 委托传入。监控引擎轴（IDatabaseProvider 实现）独立于本方法——
/// 引用引擎包（DBPilot.SqlServer / DBPilot.MySql / DBPilot.Sqlite）即由自动发现注册 Provider 与存储模块
/// （AddDbpilotEngines；Sqlite 仅存储模块无 Provider），显式注册 AddDbpilotSqlServer()/AddDbpilotMySql()
/// 可混用（重复引擎先注册者优先）。
/// </summary>
public static class DbpilotUmbrellaExtension
{
    /// <summary>
    /// 全家桶 Add：Logging + Services + Storage + Auth（无条件，Web/Collector 都依赖）+ Web/Collector 分支。
    /// 开关取值优先级：内置默认(true) ← DBPilot:Roles / DBPilot:AutoInitSchema 配置（本方法自动读取，
    /// 键缺失即用默认）← 委托（显式赋值最终生效）。
    /// </summary>
    public static WebApplicationBuilder AddDBPilot(this WebApplicationBuilder builder, Action<DbpilotOptions>? configure = null)
    {
        var options = DbpilotOptions.ReadDefaults(builder.Configuration);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // PlatformEngine 无默认值：委托未写回落 DBPilot:PlatformEngine 配置（自动读取），两者皆空启动期即抛
        var engine = builder.ResolvePlatformEngine(DbpilotEngines.ToName(options.PlatformEngine));

        builder.AddDbpilotLogging()
               .AddDbpilotServices(engine)
               .AddDbpilotStorage(engine)
               .AddDbpilotAuth();          // 登录 + 实例凭据加密（幂等；Web/Collector 都要用）

        if (options.Web)
        {
            builder.AddDbpilotWeb();       // 内含 Auth（幂等）
            if (options.Mcp)
                builder.AddDbpilotMcp();
        }

        if (options.Collector)
        {
            if (options.InstanceMetrics)
                builder.AddDbpilotInstanceMetrics();
            builder.AddDbpilotQuartz();
        }

        return builder;
    }

    /// <summary>全家桶 Use：（InitSchema? 平台库幂等建表）→（Web? 静态/认证/路由/SPA + MCP 挂载）。</summary>
    public static WebApplication UseDBPilot(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<DbpilotOptions>();

        if (options.InitSchema)
            app.InitDbpilotSchema();

        if (options.Web)
            app.UseDbpilot()               // Swagger(dev)/静态文件/认证/路由/SPA fallback
               .UseDbpilotMcp();            // McpOptions 未注册（无 ApiKey）时 no-op

        return app;
    }
}
