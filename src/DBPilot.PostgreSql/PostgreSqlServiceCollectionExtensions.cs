using DBPilot.Core.Providers;
using DBPilot.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.PostgreSql;

/// <summary>
/// PostgreSQL 引擎注册（一个包 = 监控 Provider + 平台库存储模块，双轴同 MySql 形态）。
/// 默认无需手动调用——AddDBPilot 全家桶自动扫描输出目录 DBPilot.*.dll 完成注册（引用即生效）；
/// 本方法供细粒度组合（不走全家桶/自动发现被禁用）时显式接入。
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>注册 PostgreSqlProvider（监控路由）+ PostgreSqlStorageModule（平台库存储），幂等。</summary>
    public static IServiceCollection AddDbpilotPostgreSql(this IServiceCollection services)
        => services.AddDbpilotEngine<PostgreSqlProvider>()
                   .AddDbpilotStorageModule(new PostgreSqlStorageModule());
}
