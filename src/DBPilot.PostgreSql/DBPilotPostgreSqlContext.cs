using System.Data;
using Chloe.Infrastructure;
using Chloe.PostgreSQL;
using Npgsql;

namespace DBPilot.PostgreSql;

/// <summary>
/// 平台库访问上下文（Chloe / PostgreSQL；引擎包提供，随 DBPilot.PostgreSql 分发）。
/// PostgreSQLContext 无连接串便捷构造，经 IDbConnectionFactory 注入（Npgsql 驱动）。
/// </summary>
public class DBPilotPostgreSqlContext : PostgreSQLContext
{
    public DBPilotPostgreSqlContext(string connString)
        : base(new DbConnectionFactory(() => new UtcKindConnection(connString)))
    {
    }

    private sealed class DbConnectionFactory(Func<IDbConnection> factory) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => factory();
    }
}
