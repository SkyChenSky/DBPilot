using System.Reflection;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Core.TopSql;
using DBPilot.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.UnitTests;

/// <summary>
/// PostgreSQL Provider 单测：TestConnection 自检纯函数 / 指标快照映射 / TopSQL SQL 构造 /
/// 会话采样常量口径 / 版本解析 / 能力矩阵声明与 fail-closed。
/// </summary>
public class PostgreSqlProviderTests
{
    // ---------------- TestConnection 自检纯函数 ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("top")]
    [InlineData("all")]
    [InlineData("")]
    public void EvaluatePgssTrack_非none_放行无缺失(string? track)
        => Assert.Empty(PostgreSqlProvider.EvaluatePgssTrack(track));

    [Fact]
    public void EvaluatePgssTrack_none_列静默空榜症状与参数组指引()
    {
        var list = PostgreSqlProvider.EvaluatePgssTrack("none");
        var mp = Assert.Single(list);
        Assert.Contains("pg_stat_statements.track", mp.Permission);
        Assert.Contains("恒空", mp.Impact);
        Assert.Contains("参数组", mp.FixScript);
    }

    [Fact]
    public void EvaluateConnectDbs_无缺库_放行()
        => Assert.Empty(PostgreSqlProvider.EvaluateConnectDbs([]));

    [Fact]
    public void EvaluateConnectDbs_缺CONNECT的库逐个列出()
    {
        var list = PostgreSqlProvider.EvaluateConnectDbs(["db1", "db2"]);
        var mp = Assert.Single(list);
        Assert.Contains("db1, db2", mp.Permission);
        Assert.Contains("2 个库", mp.Impact);
        Assert.Contains("GRANT CONNECT", mp.FixScript);
    }

    // ---------------- 版本解析 ----------------

    [Theory]
    [InlineData("180004", 18)]
    [InlineData("130000", 13)]
    [InlineData("150002", 15)]
    [InlineData(null, 0)]
    [InlineData("abc", 0)]
    public void ParsePostgreSqlVersion_server_version_num_整除一万(string? versionNum, int expected)
        => Assert.Equal(expected, PostgreSqlProvider.ParsePostgreSqlVersion(versionNum));

    // ---------------- 指标快照映射（pg_stat_database 聚合 → SQL Server 计数器名） ----------------

    [Fact]
    public void BuildMetricsSnapshot_计数器名与命中率配对()
    {
        var snapshot = PostgreSqlProvider.BuildMetricsSnapshot("PostgreSQL 18.4", 1000, 900, 100, 3, 5, 7);

        Assert.Equal("PostgreSQL 18.4", snapshot.ProductVersion);
        Assert.Equal(100, snapshot.IoReads);

        var counters = snapshot.Counters.ToDictionary(c => c.CounterName!, c => c.CntrValue);
        // QPS 口径 = 事务域（xact_commit+xact_rollback，与 SS 语句域不同，文档注明）
        Assert.Equal(1000, counters["Batch Requests/sec"]);
        Assert.Equal(1000, counters["Transactions/sec"]);
        Assert.Equal(5, counters["Full Scans/sec"]);
        Assert.Equal(3, counters["Number of Deadlocks/sec"]);   // 死锁页趋势-only 降级形态的数据源
        Assert.Equal(7, counters["User Connections"]);
        Assert.Equal(900, counters["Buffer cache hit ratio"]);
        Assert.Equal(1000, counters["Buffer cache hit ratio base"]);   // value/base 配对（平台侧 ×100）
    }

    [Fact]
    public void BuildMetricsSnapshot_零IO_无命中率配对_IoReads为null()
    {
        var snapshot = PostgreSqlProvider.BuildMetricsSnapshot(null, 0, 0, 0, 0, 0, 0);
        Assert.Null(snapshot.IoReads);
        Assert.DoesNotContain(snapshot.Counters, c => c.CounterName == "Buffer cache hit ratio");
        Assert.DoesNotContain(snapshot.Counters, c => c.CounterName == "Buffer cache hit ratio base");
    }

    // ---------------- Top SQL 构造（pg_stat_statements） ----------------

    [Fact]
    public void BuildTopSqlSql_基础口径_to_hex指纹_毫秒转微秒_噪音排除内联()
    {
        var sql = PostgreSqlProvider.BuildTopSqlSql(new TopSqlFilter());
        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("to_hex(s.queryid)                                AS Fingerprint", sql);
        Assert.Contains("(s.total_exec_time * 1000)::bigint               AS TotalElapsedUs", sql);   // pgs 毫秒 → µs
        Assert.Contains("0::bigint                                        AS TotalWorkerUs", sql);        // PG 无 CPU 计时
        Assert.Contains("NULL::timestamptz                                AS LastExecutionTime", sql);   // pgs 1.12 无 last exec 列
        // 噪音排除内联：平台标记 + Chloe 双引号标识符签名 + RDS 自留前缀 + 内部账号
        Assert.Contains("s.query NOT LIKE '%/* dbpilot */%'", sql);
        Assert.Contains("s.query NOT LIKE '%\"dbpilot_%'", sql);
        Assert.Contains("s.query NOT LIKE '/* rds internal mark */%'", sql);
        Assert.Contains("rolname IN ('aurora', 'rdsadmin')", sql);
        // 无黑名单/模式时不出 NOT IN / NOT LIKE 子句
        Assert.DoesNotContain("to_hex(s.queryid) NOT IN", sql);
    }

    [Fact]
    public void BuildTopSqlSql_指纹黑名单_仅小写hex_转义单引号()
    {
        var sql = PostgreSqlProvider.BuildTopSqlSql(new TopSqlFilter
        {
            Fingerprints = ["abc123", "ABC", "x'y", "  ", null!],
        });
        // 仅合法小写 hex 进黑名单（与页面"排除"按钮存储格式一致）；大写/含引号/空白被过滤
        Assert.Contains("to_hex(s.queryid) NOT IN ('abc123')", sql);
        Assert.DoesNotContain("'ABC'", sql);
        Assert.DoesNotContain("x''y", sql);
    }

    [Fact]
    public void BuildTopSqlSql_特征模式_作用于归一化模板_单引号转义()
    {
        var sql = PostgreSqlProvider.BuildTopSqlSql(new TopSqlFilter
        {
            Patterns = ["%SET NAMES%", "it's"],
        });
        Assert.Contains("AND s.query NOT LIKE '%SET NAMES%'", sql);
        Assert.Contains("AND s.query NOT LIKE 'it''s'", sql);
    }

    // ---------------- 会话/阻塞采样口径 ----------------

    [Fact]
    public void ActiveRequestsSql_噪音排除与等待合成口径()
    {
        var sql = PostgreSqlProvider.ActiveRequestsSql;
        // 自监控识别：application_name 连接属性通道（优于 MySQL 语句特征兜底）+ 内联标记 + 双引号签名
        Assert.Contains("a.application_name <> 'DBPilot'", sql);
        Assert.Contains("a.pid <> pg_backend_pid()", sql);
        Assert.Contains("COALESCE(a.query, '') NOT LIKE '%/* dbpilot */%'", sql);
        Assert.Contains("COALESCE(a.query, '') NOT LIKE '%\"dbpilot_%'", sql);
        // RDS 内部账号 + 官方自留标记
        Assert.Contains("a.usename NOT IN ('aurora', 'rdsadmin')", sql);
        Assert.Contains("COALESCE(a.query, '') NOT LIKE '/* rds internal mark */%'", sql);
        // 等待分桶合成词汇：Lock → 'Waiting for … lock'（BucketOf 归 lock 桶）、其余 → 'Waiting for …'
        Assert.Contains("'Waiting for ' || a.wait_event || ' lock'", sql);
        Assert.Contains("'Waiting for ' || a.wait_event", sql);
        // 阻塞边：pg_blocking_pids 数组取头；持锁者已断的孤儿预备事务 -2 系统节点
        Assert.Contains("unnest(pg_blocking_pids(a.pid))", sql);
        Assert.Contains("THEN -2 ELSE 0 END", sql);
        // 睡着头走补查：主查询只回 active
        Assert.Contains("a.state = 'active'", sql);
    }

    [Fact]
    public void IndexUsageSql_按库连接采集_真实页数_写计数表级()
    {
        var sql = PostgreSqlProvider.IndexUsageSql;
        Assert.Contains("FROM pg_stat_user_indexes s", sql);
        Assert.Contains("JOIN pg_index i ON i.indexrelid = s.indexrelid", sql);
        // 页数 = 真实字节数 / 8192（优于 MySQL 的 null）
        Assert.Contains("(pg_relation_size(i.indexrelid) / 8192) AS UsedPages", sql);
        // 写计数从表级 pg_stat_user_tables 取（PG 索引视图无写维护计数）
        Assert.Contains("COALESCE(tu.n_tup_ins, 0) + COALESCE(tu.n_tup_upd, 0) + COALESCE(tu.n_tup_del, 0) AS TableWrites", sql);
    }

    // ---------------- 连接串 ----------------

    [Fact]
    public void BuildConnectionString_默认postgres库_带ApplicationName()
    {
        var cfg = new InstanceConfig { Host = "h", Port = 5432, LoginName = "u", Password = "p" };
        var cs = PostgreSqlProvider.BuildConnectionString(cfg);
        Assert.Contains("Host=h", cs);
        Assert.Contains("Port=5432", cs);
        Assert.Contains("Username=u", cs);
        Assert.Contains("Database=postgres", cs);
        Assert.Contains("Application Name=DBPilot", cs);   // pg_stat_activity 自监控识别口径
    }

    [Fact]
    public void BuildConnectionString_指定initialCatalog_切目标库()
    {
        var cfg = new InstanceConfig { Host = "h", Port = 5432, LoginName = "u", Password = "p" };
        Assert.Contains("Database=skydb", PostgreSqlProvider.BuildConnectionString(cfg, "skydb"));
    }

    // ---------------- 能力边界 ----------------

    [Fact]
    public async Task 无对等数据源方法_抛Unsupported_文案含引擎与功能名()
    {
        var provider = new PostgreSqlProvider();
        foreach (var call in new Func<Task>[]
                 {
                     () => provider.GetMissingIndexesAsync(new InstanceConfig(), "db"),
                     () => provider.GetFragmentTablesAsync(new InstanceConfig(), "db", 100),
                     () => provider.GetIndexFragmentationAsync(new InstanceConfig(), "db", 1, 100),
                     () => provider.ReadDeadlockEventsAsync(new InstanceConfig(), null),
                     () => provider.GetQueryPlanStatsAsync(new InstanceConfig()),
                     () => provider.GetQueryPlanXmlsAsync(new InstanceConfig(), []),
                     () => provider.EnsureSlowSqlCaptureAsync(new InstanceConfig()),
                     () => provider.PollSlowSqlAsync(new InstanceConfig(), null),
                 })
        {
            var ex = await Assert.ThrowsAsync<DbpilotUnsupportedException>(call);
            Assert.Equal("postgresql", ex.Engine);
            Assert.Contains("不支持", ex.Message);
        }
    }

    [Fact]
    public void 引擎特性声明_PG五项不支持与MySql同清单()
    {
        var pg = typeof(PostgreSqlProvider).GetCustomAttribute<DbpilotEngineAttribute>()!;
        Assert.Equal("postgresql", pg.Engine);
        Assert.Equal(
        [
            DbpilotFeatures.DeadlockEvents, DbpilotFeatures.QueryPlanSnapshot,
            DbpilotFeatures.MissingIndex, DbpilotFeatures.Fragmentation, DbpilotFeatures.SlowSqlXeChannel,
        ], pg.UnsupportedFeatures);
    }

    [Fact]
    public void 能力矩阵_PG减法与降级形态_未声明键默认Full()
    {
        var services = new ServiceCollection();
        services.AddDbpilotEngine<DBPilot.SqlServer.SqlServerProvider>();
        services.AddDbpilotEngine<PostgreSqlProvider>();
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<ProviderRegistry>();

        // 减法（None）：死锁事件/计划快照/缺失索引/碎片/慢SQL事件/OS CPU内存/PLE/编译/阻塞计数/禁用脚本
        foreach (var key in new[]
                 {
                     DbpilotCapabilityKeys.DeadlockEvents, DbpilotCapabilityKeys.QueryPlanSnapshot,
                     DbpilotCapabilityKeys.MissingIndex, DbpilotCapabilityKeys.Fragmentation,
                     DbpilotCapabilityKeys.SlowSqlEvents, DbpilotCapabilityKeys.OsCpuMem,
                     DbpilotCapabilityKeys.Ple, DbpilotCapabilityKeys.CompileStats,
                     DbpilotCapabilityKeys.BlockedProcesses, DbpilotCapabilityKeys.IndexDisableScript,
                 })
            Assert.Equal(DbpilotCapabilityLevel.None, registry.CapabilityOf("postgresql", key));

        // 同页降级形态（未声明默认 Full）：死锁趋势-only / 慢SQL模板榜
        Assert.Equal(DbpilotCapabilityLevel.Full, registry.CapabilityOf("postgresql", DbpilotCapabilityKeys.DeadlockTrend));
        Assert.Equal(DbpilotCapabilityLevel.Full, registry.CapabilityOf("postgresql", DbpilotCapabilityKeys.SlowSqlTemplates));

        // SqlServer 全 Full；未注册引擎 fail-closed None
        Assert.Equal(DbpilotCapabilityLevel.Full, registry.CapabilityOf(null, DbpilotCapabilityKeys.DeadlockEvents));
        Assert.Equal(DbpilotCapabilityLevel.None, registry.CapabilityOf("fake-engine", DbpilotCapabilityKeys.DeadlockTrend));
    }
}
