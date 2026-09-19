using System.Data;
using Chloe.Infrastructure;
using Chloe.MySql;
using MySqlConnector;

namespace DBPilot.MySql;

/// <summary>
/// 平台库访问上下文（Chloe / MySQL；引擎包重构后随 DBPilot.MySql 分发）。
/// MySqlContext 无连接串便捷构造，经 IDbConnectionFactory 注入（MySqlConnector 驱动）。
/// </summary>
public class DBPilotMySqlContext : MySqlContext
{
    public DBPilotMySqlContext(string connString)
        : base(new DbConnectionFactory(() => new MySqlConnection(connString)))
    {
    }

    private sealed class DbConnectionFactory(Func<IDbConnection> factory) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => factory();
    }
}
