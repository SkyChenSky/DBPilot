using Chloe.Infrastructure;
using Chloe.SqlServer;

namespace DBPilot.SqlServer;

/// <summary>
/// 平台库访问上下文（Chloe / SQL Server；引擎包重构后随 DBPilot.SqlServer 分发）。
/// 平台库连接串固定，实例连接由 Provider 动态创建，不经过本上下文。
/// </summary>
public class DBPilotSqlServerContext : MsSqlContext
{
    public DBPilotSqlServerContext(string connString)
        : base(connString)
    {
    }

    public DBPilotSqlServerContext(IDbConnectionFactory dbConnectionFactory)
        : base(dbConnectionFactory)
    {
    }
}
