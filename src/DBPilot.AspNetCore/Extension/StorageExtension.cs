using Chloe;
using DBPilot.Storage;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 平台库存储模块：连接串来自 appsettings DBPilot:ConnectionString，为空则不注册 DbContext
/// （各服务走"平台库未配置"分支）。引擎经 <see cref="EngineResolution"/> 解析（显式传入优先，
/// 回落 DBPilot:PlatformEngine 配置；无默认值，缺失启动期即抛），注册基类 Chloe.DbContext——
/// Core 服务不感知平台库引擎（被监控实例引擎由 ProviderRegistry 路由，两轴独立）。
/// </summary>
public static class StorageExtension
{
    public static WebApplicationBuilder AddDbpilotStorage(this WebApplicationBuilder builder, string? platformEngine = null)
    {
        // 引擎解析无条件执行（fail-fast：连接串为空也校验，与方言注册同口径）
        var module = builder.Services.ResolveStorageModule(builder.ResolvePlatformEngine(platformEngine));

        var connStr = builder.Configuration.GetSection(DbpilotConfigKeys.ConnectionString).Get<string>();
        if (string.IsNullOrWhiteSpace(connStr))
            return builder;

        builder.Services.AddScoped<DbContext>(_ => module.CreateContext(connStr!));
        return builder;
    }
}
