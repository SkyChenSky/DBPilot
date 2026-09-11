using System.Reflection;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 引擎自动发现注册（引擎包重构）：扫描输出目录 DBPilot.*.dll，反射发现
/// [DbpilotEngine] Provider（→ ProviderRegistry 监控路由）与 DbpilotStorageModule 子类
/// （→ PlatformStorageRegistry 平台库存储）——引用哪个引擎包即获得该引擎的完整能力，零接入代码。
/// 显式注册（AddDbpilotSqlServer()/AddDbpilotMySql()）与自动发现可混用：重复引擎先注册者优先，幂等。
/// 单文件发布（SelfContained single file）不适用本机制（程序集打包进宿主，无独立 DLL）。
/// </summary>
public static class EngineDiscoveryExtension
{
    /// <summary>扫描标记（幂等守卫：多次调用只扫一次）。</summary>
    private sealed class EngineScanMarker;

    public static IServiceCollection AddDbpilotEngines(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(EngineScanMarker)))
            return services;
        services.AddSingleton<EngineScanMarker>();

        foreach (var assembly in LoadDbpilotAssemblies())
        {
            Type[] types;
            try { types = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
            catch { continue; }   // 无导出类型/加载异常：尽力而为跳过

            foreach (var type in types.Where(t => t.IsClass && !t.IsAbstract))
            {
                if (typeof(IDatabaseProvider).IsAssignableFrom(type)
                    && type.GetCustomAttribute<DbpilotEngineAttribute>() is not null)
                    services.AddDbpilotEngine(type);

                if (typeof(DbpilotStorageModule).IsAssignableFrom(type)
                    && type.GetConstructor([]) is not null)
                    services.AddDbpilotStorageModule((DbpilotStorageModule)Activator.CreateInstance(type)!);
            }
        }

        return services;
    }

    /// <summary>加载输出目录全部 DBPilot.*.dll（主包/引擎包/Sample 皆匹配；已加载的返回同一实例）。</summary>
    private static List<Assembly> LoadDbpilotAssemblies()
    {
        var result = new List<Assembly>();
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "DBPilot.*.dll"))
        {
            try { result.Add(Assembly.LoadFrom(dll)); }
            catch { /* 非托管/损坏文件：跳过 */ }
        }
        return result;
    }
}

/// <summary>平台库引擎解析（StorageExtension / DependenceExtension / 全家桶共用；启动期 fail-fast）。</summary>
internal static class EngineResolution
{
    /// <summary>
    /// 解析平台库引擎（显式传入优先，回落 AddDBPilot 自动读取的 DBPilot:PlatformEngine 配置；trim）。
    /// 空 = 配置错误，启动期即抛——PlatformEngine 无默认值是硬约束，避免"静默落 sqlserver"造成的引擎错配。
    /// </summary>
    public static string ResolvePlatformEngine(this WebApplicationBuilder builder, string? explicitEngine = null)
    {
        var engine = (explicitEngine ?? builder.Configuration.GetSection(DbpilotConfigKeys.PlatformEngine).Get<string>())?.Trim();
        if (string.IsNullOrEmpty(engine))
            throw new InvalidOperationException(
                "未指定平台库引擎（PlatformEngine 无默认值，必须显式指定）——"
                + "全家桶写 DbpilotOptions.PlatformEngine（如 DbpilotEngine.SqlServer），配置形态写 DBPilot:PlatformEngine");

        return engine!;
    }

    /// <summary>
    /// 按引擎取存储模块（未扫描过则先补自动发现）。模块均为实例单例注册，直接从描述符收集即可校验，
    /// 无需构建 provider——引擎未注册（包未引用/拼写错误）启动期即抛。
    /// </summary>
    public static DbpilotStorageModule ResolveStorageModule(this IServiceCollection services, string engine)
    {
        services.AddDbpilotEngines();

        var modules = services
            .Where(d => d.ServiceType == typeof(DbpilotStorageModule) && d.ImplementationInstance is DbpilotStorageModule)
            .Select(d => (DbpilotStorageModule)d.ImplementationInstance!)
            .ToList();

        var module = modules.FirstOrDefault(m => string.Equals(m.Engine, engine, StringComparison.OrdinalIgnoreCase));
        if (module is null)
            throw new InvalidOperationException(
                $"未找到平台库引擎「{engine}」的存储模块（已注册：{string.Join(" / ", modules.Select(m => m.Engine))}）"
                + "——请确认已引用对应引擎包（DBPilot.SqlServer / DBPilot.MySql / DBPilot.PostgreSql / DBPilot.Sqlite）");

        return module;
    }
}
