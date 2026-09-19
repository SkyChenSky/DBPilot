using Chloe;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.Logging;

namespace DBPilot.MySql;

/// <summary>
/// MySQL 平台库存储模块：上下文 + 方言 + schema 初始化打包随引擎包分发
/// （DBPilot:PlatformEngine=mysql 时经 PlatformStorageRegistry 解析）。
/// </summary>
public sealed class MySqlStorageModule : DbpilotStorageModule
{
    public override string Engine => DbpilotEngines.MySql;

    public override DbContext CreateContext(string connString) => new DBPilotMySqlContext(connString);

    public override IPlatformDialect CreateDialect() => new MySqlDialect();

    public override ISchemaInitializer CreateInitializer(string connString, ILogger? logger = null)
        => new MySqlSchemaInitializer(connString, logger);
}
