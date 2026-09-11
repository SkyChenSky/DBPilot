using DBPilot.AspNetCore.Mcp;
using Serilog;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// MCP 出口模块：把实例数据暴露给程序化消费。
/// 注册条件 = ApiKey 非空（安全默认关，无独立 Enabled 开关）；两段结构——
/// Add 段注册服务（未启用则 Use 段凭 McpOptions 未注册自动 no-op）。
/// </summary>
public static class McpExtension
{
    public static WebApplicationBuilder AddDbpilotMcp(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            Log.Information("MCP 出口模块未配置 ApiKey（DBPilot:Mcp:ApiKey 为空），跳过注册——安全默认关");
            return builder;
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dbpilot", Version = "1.0" };
            })
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(DiagnosticTools).Assembly);

        Log.Information("MCP 出口已注册：路由 /mcp，认证 X-Api-Key");
        return builder;
    }

    /// <summary>挂载 /mcp（含 API key 中间件）；Add 段未启用（McpOptions 未注册）时 no-op。</summary>
    public static WebApplication UseDbpilotMcp(this WebApplication app)
    {
        if (app.Services.GetService<McpOptions>() is null)
            return app;

        app.UseMiddleware<McpAuthMiddleware>();
        app.MapMcp("/mcp");
        return app;
    }
}
