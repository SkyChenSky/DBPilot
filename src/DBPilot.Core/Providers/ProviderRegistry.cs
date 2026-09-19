using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DBPilot.Core.Providers;

/// <summary>引擎注册项（engine → Provider 具体类型 + 不支持功能集 + UI 能力矩阵；经 DI 单例收集进 ProviderRegistry）。</summary>
public sealed record ProviderRegistration(
    string Engine, Type ProviderType, IReadOnlySet<string>? UnsupportedFeatures = null,
    IReadOnlyDictionary<string, DbpilotCapabilityLevel>? Capabilities = null);

/// <summary>
/// 引擎注册表：engine 字符串 → Provider 具体类型的映射，由引擎注册
/// （自动发现扫描 / AddDbpilotSqlServer()/AddDbpilotMySql() 显式）的注册项经 DI 构造注入汇总。
/// 同引擎重复注册（显式 + 自动发现混用）先注册者优先，不抛错。Provider 实例本身是 Scoped
/// （Chloe 上下文等按 scope 解析），本表只存类型不存实例。
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, Type> _map;
    private readonly Dictionary<string, HashSet<string>> _unsupported;
    private readonly Dictionary<string, Dictionary<string, DbpilotCapabilityLevel>> _capabilities;

    public ProviderRegistry(IEnumerable<ProviderRegistration> registrations)
    {
        var grouped = registrations
            .GroupBy(r => r.Engine, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _map = grouped.ToDictionary(g => g.Key, g => g.First().ProviderType, StringComparer.OrdinalIgnoreCase);
        _unsupported = grouped.ToDictionary(
            g => g.Key,
            g => new HashSet<string>(
                g.First().UnsupportedFeatures ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        _capabilities = grouped.ToDictionary(
            g => g.Key,
            g => new Dictionary<string, DbpilotCapabilityLevel>(
                g.First().Capabilities ?? Enumerable.Empty<KeyValuePair<string, DbpilotCapabilityLevel>>(),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>已注册引擎（小写原样，日志/报错提示用）。</summary>
    public IReadOnlyCollection<string> Engines => _map.Keys.ToArray();

    /// <summary>
    /// 引擎 UI 能力矩阵（/api/engines 输出）：engine → 键 → 级别。
    /// 未注册引擎不在返回内（调用方 fail-closed）；已注册引擎未声明的键默认 Full。
    /// </summary>
    public IReadOnlyDictionary<string, Dictionary<string, DbpilotCapabilityLevel>> Capabilities => _capabilities;

    /// <summary>
    /// 单键能力判定：engine 空/白 = sqlserver（与 <see cref="Supports"/> 同口径）；
    /// 已注册引擎未声明的键默认 Full（声明式减法——只列 None/Degraded）；
    /// 未注册引擎 fail-closed 返回 None（能力未知即不支持）。
    /// </summary>
    public DbpilotCapabilityLevel CapabilityOf(string? engine, string key)
    {
        var e = string.IsNullOrWhiteSpace(engine) ? DbpilotEngines.SqlServer : engine;
        if (!_capabilities.TryGetValue(e, out var map)) return DbpilotCapabilityLevel.None;
        return map.TryGetValue(key, out var level) ? level : DbpilotCapabilityLevel.Full;
    }

    /// <summary>
    /// 引擎能力判定（能力矩阵由引擎包经 [DbpilotEngine].UnsupportedFeatures 自声明）：
    /// engine 空/白 = sqlserver（dbpilot_instance.engine 口径）；未注册引擎 fail-closed 返回 false
    /// （与 Resolve 抛 Unsupported 同源——能力未知即不支持）；匹配 OrdinalIgnoreCase 与 Resolve 一致。
    /// </summary>
    public bool Supports(string? engine, string feature)
    {
        var e = string.IsNullOrWhiteSpace(engine) ? DbpilotEngines.SqlServer : engine;
        return _unsupported.TryGetValue(e, out var set) && !set.Contains(feature);
    }

    /// <summary>按 engine 解析 Provider 实例（从当前 scope 取具体类型注册）；未注册抛 DbpilotUnsupportedException。</summary>
    public IDatabaseProvider Resolve(IServiceProvider sp, string? engine)
    {
        if (!string.IsNullOrWhiteSpace(engine) && _map.TryGetValue(engine!, out var type))
            return (IDatabaseProvider)sp.GetRequiredService(type);

        throw DbpilotUnsupportedException.NotRegistered(engine, Engines);
    }
}

public static class ProviderRegistryExtensions
{
    /// <summary>
    /// 注册引擎 Provider（非泛型重载：自动发现扫描反射调用）：具体类型 Scoped 自注册 + 注册项单例 +
    /// 路由器接管 IDatabaseProvider，幂等可多次调用不同引擎。引擎名读取 Provider 类上的
    /// <see cref="DbpilotEngineAttribute"/>（缺失抛 InvalidOperationException，启动期即暴露）。
    /// </summary>
    public static IServiceCollection AddDbpilotEngine(this IServiceCollection services, Type providerType)
    {
        if (!typeof(IDatabaseProvider).IsAssignableFrom(providerType))
            throw new InvalidOperationException($"{providerType.Name} 未实现 IDatabaseProvider");

        var attr = providerType.GetCustomAttribute<DbpilotEngineAttribute>()
            ?? throw new InvalidOperationException($"{providerType.Name} 缺少 [DbpilotEngine] 引擎标识特性");

        var capsAttr = providerType.GetCustomAttribute<DbpilotCapabilitiesAttribute>();
        var capabilities = new Dictionary<string, DbpilotCapabilityLevel>(StringComparer.OrdinalIgnoreCase);
        if (capsAttr is not null)
        {
            foreach (var key in capsAttr.Degraded) capabilities[key] = DbpilotCapabilityLevel.Degraded;
            foreach (var key in capsAttr.None) capabilities[key] = DbpilotCapabilityLevel.None;
        }

        services.AddScoped(providerType);
        services.AddSingleton(new ProviderRegistration(
            attr.Engine, providerType, attr.UnsupportedFeatures.ToHashSet(StringComparer.OrdinalIgnoreCase), capabilities));
        services.TryAddSingleton<ProviderRegistry>();
        services.TryAddScoped<IDatabaseProvider, RoutingDatabaseProvider>();
        return services;
    }

    /// <summary>注册引擎 Provider（泛型便捷重载：显式接入时编译期约束类型）。</summary>
    public static IServiceCollection AddDbpilotEngine<TProvider>(this IServiceCollection services)
        where TProvider : class, IDatabaseProvider
        => services.AddDbpilotEngine(typeof(TProvider));
}
