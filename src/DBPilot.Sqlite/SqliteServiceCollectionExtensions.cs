using DBPilot.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Sqlite;

/// <summary>
/// SQLite 引擎注册（仅平台库轴：本包无监控 Provider，不调 AddDbpilotEngine）。
/// 默认无需手动调用——AddDBPilot 全家桶自动扫描输出目录 DBPilot.*.dll 发现存储模块（引用即生效）；
/// 本方法供细粒度组合（不走全家桶/自动发现被禁用）时显式接入。
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>注册 SqliteStorageModule（平台库存储），幂等。</summary>
    public static IServiceCollection AddDbpilotSqlite(this IServiceCollection services)
        => services.AddDbpilotStorageModule(new SqliteStorageModule());
}
