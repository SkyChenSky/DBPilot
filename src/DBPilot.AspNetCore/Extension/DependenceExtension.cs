using System.Reflection;
using DBPilot.Common;
using DBPilot.Core.Auth;
using DBPilot.Core.Housekeeping;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.TopSql;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 依赖注入扫描注册：显式传入程序集，扫描其中实现标记接口的非抽象类——
/// IDepend → Scoped（业务服务）；ISingletonDepend → Singleton（无参进程内状态存储；
/// 同时实现两者时以 Singleton 为准）。显式程序集参数，避免 AppDomain.GetAssemblies() 的不确定性。
/// </summary>
public static class DependenceExtension
{
    /// <summary>Host 入口：Core / Storage / AspNetCore 三程序集扫描（引擎程序集已退出扫描，改由自动发现/引擎包显式注册）。</summary>
    public static WebApplicationBuilder AddDbpilotServices(this WebApplicationBuilder builder, string? platformEngine = null)
    {
        // 配置对象在角色无关处注册：TopSqlExcludeOptions 被 Web 出口与采集服务共用；
        // Collect/Housekeeping 虽仅采集服务消费，但服务类经标记接口全量扫描注册（Web-only 进程也在容器里），
        // 配置对象缺注册会让 DI scope 校验直接炸——统一在此注册
        // TopSqlExcludePatterns 语义对齐 Job cron：配置缺失 = 默认全集（DefaultPatterns）、显式空数组 = 清空
        builder.Services.AddSingleton(new TopSqlExcludeOptions
        {
            Patterns = builder.Configuration.GetSection(DbpilotConfigKeys.TopSqlExcludePatterns).Get<string[]>()?.ToList()
                ?? TopSqlExcludeOptions.DefaultPatterns.ToList(),
        });
        builder.Services.AddSingleton(builder.Configuration.GetSection(DbpilotConfigKeys.Collect).Get<CollectOptions>() ?? new CollectOptions());
        builder.Services.AddSingleton(builder.Configuration.GetSection(DbpilotConfigKeys.Retention).Get<HousekeepingOptions>() ?? new HousekeepingOptions());

        // 平台库方言（B3）：引擎经 EngineResolution 解析（fail-fast，无默认值），由引擎包的存储模块供给；
        // 连接串为空也注册（语句构造无连接，服务统一走"平台库未配置"分支）
        var module = builder.Services.ResolveStorageModule(builder.ResolvePlatformEngine(platformEngine));
        builder.Services.AddSingleton<IPlatformDialect>(module.CreateDialect());

        builder.Services.AddDbpilotServices(
            typeof(PasswordHasher).Assembly,
            typeof(DbpilotStorageModule).Assembly,
            typeof(DependenceExtension).Assembly);
        return builder;
    }

    public static IServiceCollection AddDbpilotServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        var markers = new[] { typeof(IDepend), typeof(ISingletonDepend) };

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes()
                         .Where(t => t.IsClass && !t.IsAbstract && markers.Any(m => m.IsAssignableFrom(t))))
            {
                if (typeof(ISingletonDepend).IsAssignableFrom(type))
                    services.AddSingleton(type);
                else
                    services.AddScoped(type);

                // 同时注册其业务接口（如 IDatabaseProvider），排除标记接口本身
                foreach (var itf in type.GetInterfaces().Where(i => !markers.Contains(i)))
                {
                    if (typeof(ISingletonDepend).IsAssignableFrom(type))
                        services.AddSingleton(itf, type);
                    else
                        services.AddScoped(itf, type);
                }
            }
        }

        return services;
    }
}

