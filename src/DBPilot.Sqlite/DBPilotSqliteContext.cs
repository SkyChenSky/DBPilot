using System.Data;
using Chloe.Infrastructure;
using Chloe.SQLite;
using Microsoft.Data.Sqlite;

namespace DBPilot.Sqlite;

/// <summary>
/// 平台库访问上下文（Chloe / SQLite）。经 IDbConnectionFactory 注入（Microsoft.Data.Sqlite 驱动）；
/// 每个连接打开后立即设 WAL + busy_timeout——前者库级持久（幂等），后者连接级
/// （Default Timeout 不联动 busy_timeout，实测为 0，必须逐连接显式设置）。
/// 并发必须关掉 Chloe 的 ConcurrencyMode（静态进程级 ReaderWriterLockSlim）：其
/// EndTransaction/EndRead 以线程亲和的 IsWriteLockHeld/IsReadLockHeld 判定后释放，
/// 事务开启与提交落在不同线程池线程（await 续体换线程）时会静默跳过 Exit → 锁永久泄漏，
/// 同连接串全部连接死锁（实测 Host 落库全停且无任何报错）。每个上下文独立连接 +
/// WAL + busy_timeout 已覆盖本部署形态（单进程）的并发需求。
/// </summary>
public class DBPilotSqliteContext : SQLiteContext
{
    /// <summary>单进程写者模型的并发兜底（毫秒）：Web+采集同进程时页面请求与落库短暂互等而非报 SQLITE_BUSY。</summary>
    public const int BusyTimeoutMs = 5000;

    public DBPilotSqliteContext(string connString)
        : base(CreateOptions(connString))
    {
    }

    private static SQLiteOptions CreateOptions(string connString) => new()
    {
        ConcurrencyMode = false,
        DbConnectionFactory = new DbConnectionFactory(() => CreateConfiguredConnection(connString)),
    };

    internal static SqliteConnection CreateConfiguredConnection(string connString)
    {
        var conn = new SqliteConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs}; PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private sealed class DbConnectionFactory(Func<IDbConnection> factory) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => factory();
    }
}
