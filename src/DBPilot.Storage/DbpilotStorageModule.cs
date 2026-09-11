using Chloe;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DBPilot.Storage;

/// <summary>
/// 平台库存储模块（引擎包提供实现）：一个模块打包该引擎的平台库能力——
/// DbContext 上下文工厂 + 内联 SQL 方言 + schema 初始化，经 <see cref="PlatformStorageRegistry"/>
/// 按 DBPilot:PlatformEngine 解析（平台库引擎与被监控实例引擎两轴独立）。
/// 仅提供存储不提供监控的引擎（如未来的 SQLite）可只实现本模块、不实现 IDatabaseProvider。
/// </summary>
public abstract class DbpilotStorageModule
{
    /// <summary>引擎标识（dbpilot 引擎名统一小写；与 DbpilotEngines 常量一致）。</summary>
    public abstract string Engine { get; }

    /// <summary>创建平台库上下文（连接串来自 DBPilot:ConnectionString，Scoped 注册由组合层负责）。</summary>
    public abstract DbContext CreateContext(string connString);

    /// <summary>创建平台库内联 SQL 方言（Singleton）。</summary>
    public abstract IPlatformDialect CreateDialect();

    /// <summary>创建平台库结构初始化器（启动时一次性使用）。</summary>
    public abstract ISchemaInitializer CreateInitializer(string connString, ILogger? logger = null);
}

/// <summary>存储模块注册表：engine → 模块实例（各引擎包经 DI 注册模块实例，构造注入汇总；重复引擎先注册者优先）。</summary>
public sealed class PlatformStorageRegistry
{
    private readonly Dictionary<string, DbpilotStorageModule> _map;

    public PlatformStorageRegistry(IEnumerable<DbpilotStorageModule> modules)
        => _map = modules
            .GroupBy(m => m.Engine, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>已注册引擎（小写原样，日志/报错提示用）。</summary>
    public IReadOnlyCollection<string> Engines => _map.Keys.ToArray();

    /// <summary>按 engine 解析存储模块；未注册抛 InvalidOperationException（启动期即暴露配置错误）。</summary>
    public DbpilotStorageModule Resolve(string? engine)
    {
        var normalized = engine?.Trim();
        if (!string.IsNullOrEmpty(normalized) && _map.TryGetValue(normalized, out var module))
            return module;

        throw new InvalidOperationException(
            $"未找到平台库引擎「{engine}」的存储模块（已注册：{string.Join(" / ", Engines)}）——请确认已引用对应引擎包（DBPilot.SqlServer / DBPilot.MySql / DBPilot.PostgreSql / DBPilot.Sqlite）");
    }
}

public static class StorageModuleExtensions
{
    /// <summary>
    /// 注册存储模块实例（幂等可多次调用不同引擎；引擎包的 AddDbpilotSqlServer()/AddDbpilotMySql()
    /// 内部使用，宿主通常无需直接调用——自动发现/显式引擎注册已覆盖）。
    /// </summary>
    public static IServiceCollection AddDbpilotStorageModule<TModule>(this IServiceCollection services, TModule module)
        where TModule : DbpilotStorageModule
    {
        services.AddSingleton(typeof(DbpilotStorageModule), module);
        services.TryAddSingleton<PlatformStorageRegistry>();
        return services;
    }
}
