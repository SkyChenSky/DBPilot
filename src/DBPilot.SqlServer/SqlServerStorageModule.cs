using Chloe;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.Logging;

namespace DBPilot.SqlServer;

/// <summary>
/// SQL Server 平台库存储模块：上下文 + 方言 + schema 初始化打包随引擎包分发
/// （DBPilot:PlatformEngine=sqlserver 时经 PlatformStorageRegistry 解析）。
/// </summary>
public sealed class SqlServerStorageModule : DbpilotStorageModule
{
    public override string Engine => DbpilotEngines.SqlServer;

    public override DbContext CreateContext(string connString) => new DBPilotSqlServerContext(connString);

    public override IPlatformDialect CreateDialect() => new SqlServerDialect();

    public override ISchemaInitializer CreateInitializer(string connString, ILogger? logger = null)
        => new SqlServerSchemaInitializer(connString, logger);
}
