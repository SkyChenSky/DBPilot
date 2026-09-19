using DBPilot.MySql;
using DBPilot.PostgreSql;
using DBPilot.SqlServer;
using DBPilot.Sqlite;
using DBPilot.Storage.Dialect;

namespace DBPilot.UnitTests;

/// <summary>
/// 平台库方言测试：SqlServer 实现含 T-SQL 方言标记（TOP/ROW_NUMBER/DATEADD/@@ROWCOUNT），
/// MySql 实现全表面积不含任何 T-SQL 专属构造并带 LIMIT/DATE_FORMAT/ROW_COUNT 等价物；
/// Sqlite 以 MySql 为底本（strftime 桶 / rowid+LIMIT 批删 / changes()），仅平台库轴无监控 Provider；
/// PostgreSql 以 MySql 为底本（date_trunc 桶 / CTE+RETURNING 批删 / ILIKE），双轴引擎；
/// 四方言语句均带 /* dbpilot */ 自监控标记（平台库=被监控实例时的噪音排除）。
/// </summary>
public class DialectTests
{
    private static readonly SqlServerDialect Sql = new();
    private static readonly MySqlDialect My = new();
    private static readonly SqliteDialect Sq = new();
    private static readonly PostgreSqlDialect Pg = new();

    /// <summary>枚举方言全部语句（参数取典型值），逐条断言。</summary>
    private static IEnumerable<string> AllSql(IPlatformDialect d) =>
    [
        d.DeadlockPageCountSql(""),
        d.DeadlockPageCountSql("AND EXISTS (SELECT 1)"),
        d.DeadlockPageRowsSql(""),
        d.DeadlockFilterLoginsSql(),
        d.DeadlockFilterHostsSql(),
        d.DeadlockTrendTotalsSql("minute"),
        d.DeadlockTrendTotalsSql("hour"),
        d.DeadlockTrendTotalsSql("day"),
        d.DeadlockTrendColorsSql("minute"),
        d.DeadlockFingerprintStatsSql(),
        d.DeadlockObjectFilterSql(),
        d.PlanVersionsSql(),
        d.PlanChangesTopSql(),
        d.PlanChangeBoardSql(100),
        d.SlowSqlTemplatesSql("WHERE instance_id = @instanceId"),
        d.SlowSqlTemplatesFromTopSqlSql("WHERE d.instance_id = @instanceId"),
        d.SlowSqlTrendSql("hour", "AND db_name = @db"),
        d.IndexUsageLatestSnapshotSql(" AND (db_name = @db OR table_name = @marker)"),
        d.IndexUsageTrendSql("", ""),
        d.TopSqlHistorySql("q.TotalElapsedMs", 20),
        d.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000),
        d.InstanceLastErrorClearSql(),
        d.InstanceLastErrorSetSql(),
    ];

    [Fact]
    public void 四方言_全语句_带dbpilot自监控标记()
    {
        // DeadlockObjectFilterSql 是嵌入分页 SQL 的条件片段（原内联实现亦无标记），单独排除
        foreach (var sql in AllSql(Sql).Concat(AllSql(My)).Concat(AllSql(Sq)).Concat(AllSql(Pg)).Where(s => !s.Contains("EXISTS (SELECT 1 FROM dbpilot_deadlock_re")))
            Assert.Contains("/* dbpilot */", sql);
    }

    [Fact]
    public void SqlServer_方言标记_TopN_分页_桶_ROWCOUNT()
    {
        Assert.Contains("TOP (20)", Sql.TopSqlHistorySql("q.TotalElapsedMs", 20));
        Assert.Contains("TOP (100)", Sql.PlanChangeBoardSql(100));
        Assert.Contains("TOP 50", Sql.SlowSqlTemplatesSql("WHERE 1=1"));
        Assert.Contains("TOP (200)", Sql.PlanChangesTopSql());
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY e.event_time DESC)", Sql.DeadlockPageRowsSql(""));
        Assert.Contains("t.rn > @skip AND t.rn <= @skip + @take", Sql.DeadlockPageRowsSql(""));
        Assert.Contains("DATEADD(minute, DATEDIFF(minute, 0, event_time), 0)", Sql.DeadlockTrendTotalsSql("minute"));
        Assert.Contains("DELETE TOP (5000) FROM dbpilot_slow_sql WHERE event_time < @cutoff", Sql.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
        Assert.Contains("SELECT @@ROWCOUNT", Sql.HousekeepingDeleteBatchSql("t", "c", 1));
        Assert.Contains("ESCAPE N'\\'", Sql.DeadlockObjectFilterSql());   // ESCAPE N'\'（T-SQL 字面量单反斜杠）
        Assert.Contains("AS [Count]", Sql.DeadlockFingerprintStatsSql());
    }

    [Fact]
    public void MySql_方言等价物_LIMIT_桶_ROW_COUNT_反引号()
    {
        Assert.Contains("LIMIT 20", My.TopSqlHistorySql("q.TotalElapsedMs", 20));
        Assert.Contains("LIMIT 100", My.PlanChangeBoardSql(100));
        Assert.Contains("LIMIT 50", My.SlowSqlTemplatesSql("WHERE 1=1"));
        Assert.Contains("LIMIT 200", My.PlanChangesTopSql());
        Assert.Contains("ORDER BY e.event_time DESC", My.DeadlockPageRowsSql(""));
        Assert.Contains("LIMIT @skip, @take", My.DeadlockPageRowsSql(""));
        Assert.Contains("CAST(DATE_FORMAT(event_time, '%Y-%m-%d %H:%i:00') AS DATETIME(3))", My.DeadlockTrendTotalsSql("minute"));
        Assert.Contains("CAST(DATE_FORMAT(event_time, '%Y-%m-%d %H:00:00') AS DATETIME(3))", My.DeadlockTrendTotalsSql("hour"));
        Assert.Contains("CAST(DATE_FORMAT(event_time, '%Y-%m-%d 00:00:00') AS DATETIME(3))", My.DeadlockTrendTotalsSql("day"));
        Assert.Contains("DELETE FROM dbpilot_slow_sql WHERE event_time < @cutoff LIMIT 5000", My.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
        Assert.Contains("SELECT ROW_COUNT()", My.HousekeepingDeleteBatchSql("t", "c", 1));
        Assert.Contains("ESCAPE '\\\\'", My.DeadlockObjectFilterSql());   // MySQL 字面量双反斜杠 = 单反斜杠转义符
        Assert.Contains("AS `Count`", My.DeadlockFingerprintStatsSql());
    }

    [Fact]
    public void MySql_全表面积_不含TSQL专属构造()
    {
        foreach (var sql in AllSql(My))
        {
            Assert.DoesNotContain("TOP", sql);
            Assert.DoesNotContain("DATEADD", sql);
            Assert.DoesNotContain("DATEDIFF", sql);
            Assert.DoesNotContain("@@ROWCOUNT", sql);
            Assert.DoesNotContain("ESCAPE N'", sql);
        }
    }

    [Fact]
    public void Sqlite_方言等价物_LIMIT_strftime桶_rowid批删_changes()
    {
        Assert.Contains("LIMIT 20", Sq.TopSqlHistorySql("q.TotalElapsedMs", 20));
        Assert.Contains("LIMIT 100", Sq.PlanChangeBoardSql(100));
        Assert.Contains("LIMIT 50", Sq.SlowSqlTemplatesSql("WHERE 1=1"));
        Assert.Contains("LIMIT 200", Sq.PlanChangesTopSql());
        Assert.Contains("LIMIT @skip, @take", Sq.DeadlockPageRowsSql(""));
        Assert.Contains("strftime('%Y-%m-%d %H:%M:00', event_time)", Sq.DeadlockTrendTotalsSql("minute"));
        Assert.Contains("strftime('%Y-%m-%d %H:00:00', event_time)", Sq.DeadlockTrendTotalsSql("hour"));
        Assert.Contains("strftime('%Y-%m-%d 00:00:00', event_time)", Sq.DeadlockTrendTotalsSql("day"));
        Assert.Contains(
            "DELETE FROM dbpilot_slow_sql WHERE rowid IN (SELECT rowid FROM dbpilot_slow_sql WHERE event_time < @cutoff LIMIT 5000)",
            Sq.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
        Assert.Contains("SELECT changes()", Sq.HousekeepingDeleteBatchSql("t", "c", 1));
        Assert.Contains("ESCAPE '\\'", Sq.DeadlockObjectFilterSql());   // SQLite 字面量单反斜杠（反斜杠不转义）
        Assert.Contains("AS `Count`", Sq.DeadlockFingerprintStatsSql());
        Assert.Contains("substr(MAX(sql_text), 1, 500)", Sq.SlowSqlTemplatesSql("WHERE 1=1"));
        Assert.DoesNotContain("N''", Sq.PlanVersionsSql());
        Assert.DoesNotContain("N''", Sq.TopSqlHistorySql("q.TotalElapsedMs", 20));
    }

    [Fact]
    public void Sqlite_全表面积_不含TSQL与MySQL专属构造()
    {
        foreach (var sql in AllSql(Sq))
        {
            Assert.DoesNotContain("TOP", sql);
            Assert.DoesNotContain("DATEADD", sql);
            Assert.DoesNotContain("DATEDIFF", sql);
            Assert.DoesNotContain("@@ROWCOUNT", sql);
            Assert.DoesNotContain("ESCAPE N'", sql);
            Assert.DoesNotContain("N'", sql);          // 无 Unicode 前缀
            Assert.DoesNotContain("ROW_COUNT", sql);   // MySQL 计数函数
            Assert.DoesNotContain("SUBSTRING", sql);   // T-SQL/MySQL 皆用 SUBSTRING，SQLite 为 substr
            Assert.DoesNotContain("DATE_FORMAT", sql); // MySQL 桶化
            Assert.DoesNotContain("AS SIGNED", sql);   // MySQL CAST 目标类型
        }

        // SQLite 编译不带 DELETE..LIMIT：批删必须走 rowid 子查询形态
        Assert.Contains("rowid IN (SELECT rowid FROM", Sq.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
    }

    [Fact]
    public void PostgreSql_方言等价物_LIMIT_OFFSET_date_trunc桶_CTE批删_ILIKE()
    {
        Assert.Contains("LIMIT 20", Pg.TopSqlHistorySql("q.TotalElapsedMs", 20));
        Assert.Contains("LIMIT 100", Pg.PlanChangeBoardSql(100));
        Assert.Contains("LIMIT 50", Pg.SlowSqlTemplatesSql("WHERE 1=1"));
        Assert.Contains("LIMIT 200", Pg.PlanChangesTopSql());
        Assert.Contains("ORDER BY e.event_time DESC", Pg.DeadlockPageRowsSql(""));
        Assert.Contains("LIMIT @take OFFSET @skip", Pg.DeadlockPageRowsSql(""));   // PG 无双参 LIMIT 形态
        Assert.Contains("date_trunc('minute', event_time AT TIME ZONE 'utc')", Pg.DeadlockTrendTotalsSql("minute"));
        Assert.Contains("date_trunc('hour', event_time AT TIME ZONE 'utc')", Pg.DeadlockTrendTotalsSql("hour"));
        Assert.Contains("date_trunc('day', event_time AT TIME ZONE 'utc')", Pg.DeadlockTrendTotalsSql("day"));
        Assert.Contains(
            "WITH del AS (DELETE FROM dbpilot_slow_sql",
            Pg.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
        Assert.Contains(
            "WHERE id IN (SELECT id FROM dbpilot_slow_sql WHERE event_time < @cutoff LIMIT 5000)",
            Pg.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
        Assert.Contains("RETURNING 1)", Pg.HousekeepingDeleteBatchSql("t", "c", 1));
        Assert.Contains("SELECT count(*) FROM del", Pg.HousekeepingDeleteBatchSql("t", "c", 1));
        Assert.Contains("ILIKE @objectName ESCAPE '\\'", Pg.DeadlockObjectFilterSql());   // PG LIKE 大小写敏感，用户输入匹配面用 ILIKE
        Assert.Contains("AS \"Count\"", Pg.DeadlockFingerprintStatsSql());
        Assert.Contains("substr(MAX(sql_text), 1, 500)", Pg.SlowSqlTemplatesSql("WHERE 1=1"));
        Assert.Contains("@excludeSystemDb = false", Pg.TopSqlHistorySql("q.TotalElapsedMs", 20));   // PG 无 bool/int 隐式 coercion
        Assert.Contains("CAST(AVG(duration_ms) AS bigint)", Pg.SlowSqlTemplatesSql("WHERE 1=1"));
    }

    [Fact]
    public void PostgreSql_全表面积_不含TSQL与MySQL专属构造()
    {
        foreach (var sql in AllSql(Pg))
        {
            Assert.DoesNotContain("TOP", sql);
            Assert.DoesNotContain("DATEADD", sql);
            Assert.DoesNotContain("DATEDIFF", sql);
            Assert.DoesNotContain("@@ROWCOUNT", sql);
            Assert.DoesNotContain("ESCAPE N'", sql);
            Assert.DoesNotContain("N'", sql);            // 无 Unicode 前缀
            Assert.DoesNotContain("ROW_COUNT", sql);     // MySQL 计数函数
            Assert.DoesNotContain("SUBSTRING", sql);     // T-SQL/MySQL 用 SUBSTRING，PG 为 substr
            Assert.DoesNotContain("DATE_FORMAT", sql);   // MySQL 桶化
            Assert.DoesNotContain("AS SIGNED", sql);     // MySQL CAST 目标类型
            Assert.DoesNotContain("`", sql);             // MySQL 反引号标识符
            Assert.DoesNotContain("[Count]", sql);       // T-SQL 方括号标识符
            Assert.DoesNotContain("LIMIT @skip, @take", sql);   // 双参 LIMIT 是 MySQL/SQLite 形态
        }

        // PG 的 DELETE 无 LIMIT、无 ROW_COUNT()/changes() 对等物：批删必须走 CTE + RETURNING 形态
        Assert.Contains("WITH del AS (DELETE FROM", Pg.HousekeepingDeleteBatchSql("dbpilot_slow_sql", "event_time", 5000));
    }

    [Fact]
    public void MySql_未知桶粒度_抛ArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => My.DeadlockTrendTotalsSql("week"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sq.DeadlockTrendTotalsSql("week"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pg.DeadlockTrendTotalsSql("week"));
    }

    [Fact]
    public void 四方言_条件片段拼接_总数与页行条件一致透传()
    {
        var cond = "AND EXISTS (SELECT 1 FROM x WHERE x.e = e.id)";
        Assert.Contains(cond, Sql.DeadlockPageCountSql(cond));
        Assert.Contains(cond, My.DeadlockPageCountSql(cond));
        Assert.Contains(cond, Sq.DeadlockPageCountSql(cond));
        Assert.Contains(cond, Pg.DeadlockPageCountSql(cond));
        Assert.Contains(cond, Sql.DeadlockPageRowsSql(cond));
        Assert.Contains(cond, My.DeadlockPageRowsSql(cond));
        Assert.Contains(cond, Sq.DeadlockPageRowsSql(cond));
        Assert.Contains(cond, Pg.DeadlockPageRowsSql(cond));
    }
}
