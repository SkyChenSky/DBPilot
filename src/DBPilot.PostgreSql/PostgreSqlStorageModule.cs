using Chloe;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.Logging;

namespace DBPilot.PostgreSql;

/// <summary>
/// PostgreSQL 平台库存储模块：上下文 + 方言 + schema 初始化打包随引擎包分发
/// （DBPilot:PlatformEngine=postgresql 时经 PlatformStorageRegistry 解析）。
/// </summary>
public sealed class PostgreSqlStorageModule : DbpilotStorageModule
{
    public override string Engine => DbpilotEngines.PostgreSql;

    public override DbContext CreateContext(string connString) => new DBPilotPostgreSqlContext(connString);

    public override IPlatformDialect CreateDialect() => new PostgreSqlDialect();

    public override ISchemaInitializer CreateInitializer(string connString, ILogger? logger = null)
        => new PostgreSqlSchemaInitializer(connString, logger);
}
