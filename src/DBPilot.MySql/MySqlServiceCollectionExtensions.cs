using DBPilot.Core.Providers;
using DBPilot.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.MySql;

/// <summary>
/// MySQL 引擎注册（引擎包重构后语义：一个包 = 监控 Provider + 平台库存储模块）。
/// 默认无需手动调用——AddDBPilot 全家桶自动扫描输出目录 DBPilot.*.dll 完成注册（引用即生效）；
/// 本方法供细粒度组合（不走全家桶/自动发现被禁用）时显式接入。
/// </summary>
public static class MySqlServiceCollectionExtensions
{
    /// <summary>注册 MySqlProvider（监控路由）+ MySqlStorageModule（平台库存储），幂等。</summary>
    public static IServiceCollection AddDbpilotMySql(this IServiceCollection services)
        => services.AddDbpilotEngine<MySqlProvider>()
                   .AddDbpilotStorageModule(new MySqlStorageModule());
}
