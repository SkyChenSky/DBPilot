using DBPilot.AspNetCore.Auth;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.AspNetCore.Extension;

/// <summary>中间件管线：Swagger(dev) / 静态文件 / 认证 / 路由 / SPA fallback + 启动时平台库结构初始化。</summary>
public static class AppPipelineExtension
{
    /// <summary>
    /// 启动时平台库结构初始化（幂等；连接串为空跳过；是否调用由全家桶 UseDBPilot 的 InitSchema 或宿主控制）。
    /// 连接失败仅告警不阻断启动，便于前端先行联调。
    /// </summary>
    public static WebApplication InitDbpilotSchema(this WebApplication app)
    {
        var connStr = app.Configuration.GetSection(DbpilotConfigKeys.ConnectionString).Get<string>();
        if (string.IsNullOrWhiteSpace(connStr))
            return app;

        try
        {
            // 引擎优先取全家桶委托值（Add 阶段已 fail-fast 校验），细粒度路径回落配置
            var engine = DbpilotEngines.ToName(app.Services.GetService<DbpilotOptions>()?.PlatformEngine)
                ?? app.Configuration.GetSection(DbpilotConfigKeys.PlatformEngine).Get<string>();
            var module = app.Services.GetRequiredService<PlatformStorageRegistry>().Resolve(engine);
            module.CreateInitializer(connStr, app.Logger).Initialize(createDatabaseIfMissing: true);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "平台库结构初始化失败（数据库不可达时功能接口将不可用），服务继续启动");
        }

        return app;
    }

    /// <summary>
    /// 中间件管线组合入口：Web（Swagger dev + 静态文件）→ 认证 → 路由 → SPA fallback。顺序不可调换。
    /// 是否调用由全家桶 UseDBPilot 的 Web 开关 gate（false 时整个管线不挂，空管线对任何请求 404，含 health）。
    /// </summary>
    public static WebApplication UseDbpilot(this WebApplication app)
    {
        app.UseDbpilotWeb()      // Swagger(dev) / 默认文档 / 静态文件
           .UseDbpilotAuth();    // /api/** 认证拦截（login/health 放行）

        app.MapControllers();

        // SPA fallback：非 /api 路径回落到 index.html
        app.MapFallbackToFile("index.html");

        return app;
    }
}
