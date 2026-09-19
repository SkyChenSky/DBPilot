using Chloe;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.Logging;

namespace DBPilot.Sqlite;

/// <summary>
/// SQLite 平台库存储模块：上下文 + 方言 + schema 初始化打包随引擎包分发
/// （DBPilot:PlatformEngine=sqlite 时经 PlatformStorageRegistry 解析）。
/// 本模块仅平台库轴——SQLite 不做被监控实例，包内无 IDatabaseProvider 实现。
/// </summary>
public sealed class SqliteStorageModule : DbpilotStorageModule
{
    public override string Engine => DbpilotEngines.Sqlite;

    public override DbContext CreateContext(string connString) => new DBPilotSqliteContext(connString);

    public override IPlatformDialect CreateDialect() => new SqliteDialect();

    public override ISchemaInitializer CreateInitializer(string connString, ILogger? logger = null)
        => new SqliteSchemaInitializer(connString, logger);
}
