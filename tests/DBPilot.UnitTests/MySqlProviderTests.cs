using System.Text.RegularExpressions;
using DBPilot.Core.Blocking;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.SlowSql;
using DBPilot.MySql;
using MySqlConnector;

namespace DBPilot.UnitTests;

/// <summary>
/// MySQL Provider 纯函数测试（指标 / 会话阻塞）。
/// 映射的关键约束：计数器名用 SQL Server 口径（InstanceMetricsCollectService.ResolveCounters 零改动）、
/// 会话行结构对齐 dm_exec_requests 语义（BlockTreeBuilder / SampleTickBuilder 零改动）。
/// </summary>
public class MySqlProviderTests
{
    // ---- 版本解析 ----

    [Theory]
    [InlineData("8.0.36", 8)]
    [InlineData("5.7.44-log", 5)]
    [InlineData("8.4.5", 8)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("abc", 0)]
    public void ParseMySqlVersion_向量(string? version, int expected)
        => Assert.Equal(expected, MySqlProvider.ParseMySqlVersion(version));

    // ---- 连接串 ----

    [Fact]
    public void BuildConnectionString_端口_凭据_程序名_库名()
    {
        var cfg = new InstanceConfig { Host = "mysql.example.com", Port = 3306, LoginName = "sky", Password = "pwd" };
        var b = new MySqlConnectionStringBuilder(MySqlProvider.BuildConnectionString(cfg));

        Assert.Equal("mysql.example.com", b.Server);
        Assert.Equal(3306u, b.Port);
        Assert.Equal("sky", b.UserID);
        Assert.Equal("pwd", b.Password);
        Assert.Equal("DBPilot", b.ApplicationName);
        Assert.True(b.AllowPublicKeyRetrieval);
        Assert.Empty(b.Database);   // 未指定库
    }

    [Fact]
    public void BuildConnectionString_非默认端口与目标库()
    {
        var cfg = new InstanceConfig { Host = "h", Port = 3307, LoginName = "u", Password = "p" };
        var b = new MySqlConnectionStringBuilder(MySqlProvider.BuildConnectionString(cfg, "dbpilot_smoke"));

        Assert.Equal(3307u, b.Port);
        Assert.Equal("dbpilot_smoke", b.Database);
    }

    // ---- 指标映射 ----

    /// <summary>RDS 8.0.36 实测形态的状态子集（大小写混合，验证 OrdinalIgnoreCase 解析）。</summary>
    private static Dictionary<string, string> FixtureStatus() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Questions"] = "1000",
        ["Com_commit"] = "120",
        ["COM_ROLLBACK"] = "30",                     // 大小写不敏感
        ["Connections"] = "55",
        ["Select_scan"] = "77",
        ["Innodb_deadlocks"] = "2",
        ["Innodb_row_lock_timeouts"] = "3",
        ["Innodb_row_lock_waits"] = "4",
        ["Threads_connected"] = "21",
        ["Innodb_buffer_pool_read_requests"] = "9900",
        ["Innodb_buffer_pool_reads"] = "100",
        ["Innodb_buffer_pool_bytes_data"] = "2147483648",   // 2 GiB → 2097152 KB
        ["Innodb_data_reads"] = "800",
        ["Innodb_data_writes"] = "900",
        ["Innodb_data_read"] = "16384",
        ["Innodb_data_written"] = "32768",
    };

    private static long? C(InstanceMetricsSnapshot s, string name)
        => s.Counters.SingleOrDefault(r => r.CounterName == name)?.CntrValue;

    [Fact]
    public void BuildMetricsSnapshot_计数器名对齐SQLServer口径()
    {
        var s = MySqlProvider.BuildMetricsSnapshot("8.0.36", FixtureStatus());

        Assert.Equal("8.0.36", s.ProductVersion);
        Assert.Equal(1000, C(s, "Batch Requests/sec"));             // Questions
        Assert.Equal(150, C(s, "Transactions/sec"));                // Com_commit + Com_rollback
        Assert.Equal(55, C(s, "Logins/sec"));
        Assert.Equal(77, C(s, "Full Scans/sec"));
        Assert.Equal(2, C(s, "Number of Deadlocks/sec"));
        Assert.Equal(3, C(s, "Lock Timeouts/sec"));
        Assert.Equal(4, C(s, "Lock Waits/sec"));
        Assert.Equal(21, C(s, "User Connections"));

        // 命中率配对：value = 9900 − 100 = 9800，base = 9900 → 平台侧 9800/9900×100
        Assert.Equal(9800, C(s, "Buffer cache hit ratio"));
        Assert.Equal(9900, C(s, "Buffer cache hit ratio base"));

        Assert.Equal(2097152, s.SqlMemoryKb);
        Assert.Equal(800, s.IoReads);
        Assert.Equal(900, s.IoWrites);
        Assert.Equal(16384, s.IoBytesRead);
        Assert.Equal(32768, s.IoBytesWritten);
        Assert.Null(s.CpuUsagePct);                                  // 无对等 → null 降级
    }

    [Fact]
    public void BuildMetricsSnapshot_平台侧ResolveCounters互操作()
    {
        var s = MySqlProvider.BuildMetricsSnapshot("8.0.36", FixtureStatus());
        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(s);

        // 累计类（差值语义，实例重启检测靠 SqlServerStartTimeUtc）
        Assert.Equal(1000, cumulative.BatchRequests);
        Assert.Equal(150, cumulative.Transactions);
        Assert.Equal(55, cumulative.Logins);
        Assert.Equal(77, cumulative.FullScans);
        Assert.Equal(2, cumulative.Deadlocks);
        Assert.Equal(3, cumulative.LockTimeouts);
        Assert.Equal(4, cumulative.LockWaits);
        Assert.Equal(800, cumulative.IoReads);

        // 瞬时类；PLE / LazyWrites / Compilations / BlockedProcesses 无对等为 null
        Assert.Equal(21, gauges.UserConnections);
        Assert.Equal(98.99m, gauges.BufferCacheHitRatioPct);
        Assert.Null(gauges.Ple);
        Assert.Null(gauges.BlockedProcesses);
        Assert.Null(cumulative.LazyWrites);
        Assert.Null(cumulative.Compilations);
    }

    [Fact]
    public void BuildMetricsSnapshot_空状态_全null不炸()
    {
        var s = MySqlProvider.BuildMetricsSnapshot(null, new Dictionary<string, string>());

        Assert.Null(s.ProductVersion);
        Assert.Empty(s.Counters);
        Assert.Null(s.SqlMemoryKb);
        Assert.Null(s.IoReads);

        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(s);
        Assert.Null(cumulative.BatchRequests);
        Assert.Null(gauges.UserConnections);
    }

    [Fact]
    public void BuildMetricsSnapshot_物理读超过读请求_命中率钳零()
    {
        var status = new Dictionary<string, string>
        {
            ["Innodb_buffer_pool_read_requests"] = "100",
            ["Innodb_buffer_pool_reads"] = "150",   // 异常态（重启/清零）不产负值
        };

        var s = MySqlProvider.BuildMetricsSnapshot("8.0.36", status);
        Assert.Equal(0, C(s, "Buffer cache hit ratio"));
        Assert.Equal(100, C(s, "Buffer cache hit ratio base"));
    }

    [Fact]
    public void BuildMetricsSnapshot_事务单边_只取有值侧()
    {
        var status = new Dictionary<string, string> { ["Com_commit"] = "42" };
        var s = MySqlProvider.BuildMetricsSnapshot("8.0.36", status);

        Assert.Equal(42, C(s, "Transactions/sec"));
        Assert.Null(C(s, "Batch Requests/sec"));   // Questions 缺失不出行
    }

    // ---- B5 会话 / 阻塞 ----

    [Fact]
    public void ActiveRequestsSql_骨架_含标记与关键数据源()
    {
        var sql = MySqlProvider.ActiveRequestsSql;

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("performance_schema.threads", sql);
        Assert.Contains("events_statements_current", sql);
        Assert.Contains("information_schema.INNODB_TRX", sql);
        Assert.Contains("data_lock_waits", sql);                    // 阻塞边来源
        Assert.Contains("session_connect_attrs", sql);              // program_name 来源
        Assert.Contains("esc.DIGEST", sql);                         // 指纹（QueryHash）
        Assert.Contains("CONNECTION_ID()", sql);                    // 排除自身连接
        Assert.Contains("'Sleep'", sql);                            // 排除空闲/后台命令
        Assert.Contains("PROCESSLIST_USER NOT IN", sql);           // 排除 RDS 内部账号（AAS 基线防抬高）
        Assert.Contains("PROCESSLIST_USER NOT IN " + MySqlProvider.RdsInternalUsersList, sql);   // 常量拼接逐字节保真
        Assert.Contains("LIKE '/* dbpilot */%'", sql);              // 平台 Provider 语句标记排除（自监控不进采样）
        Assert.Contains("LIKE '%`dbpilot_%'", sql);                 // 平台库 Chloe 反引号写排除（(My,My) 同机形态）
    }

    [Fact]
    public void HeadBlockersSql_会话号列表内插()
    {
        var sql = MySqlProvider.HeadBlockersSql([86, 102]);

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("IN (86, 102)", sql);
        Assert.Contains("events_statements_history", sql);          // Sleep 会话最后执行语句
        Assert.Contains("TIMESTAMPDIFF(SECOND, NOW(), UTC_TIMESTAMP())", sql);   // trx_started 本地时间 → UTC
    }

    [Fact]
    public void SessionLocksSql_会话号列表内插与PENDING转WAIT()
    {
        var sql = MySqlProvider.SessionLocksSql([12]);

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("IN (12)", sql);
        Assert.Contains("performance_schema.data_locks", sql);
        Assert.Contains("IN ('PENDING', 'WAITING')", sql);          // RDS 8.0.36 实测 WAITING，PENDING 兼容
        Assert.Contains("THEN 'WAIT'", sql);                        // 对齐 BlockTreeBuilder 排序词汇
    }

    [Fact]
    public void MapActiveRequests_会话号收敛与字段透传()
    {
        var rows = new List<MySqlProvider.ActiveRequestRaw>
        {
            new()
            {
                SessionId = 42, Status = "suspended", Command = "Query",
                StartTimeUtc = new DateTime(2026, 9, 7, 1, 2, 3),
                WaitType = "Waiting for row lock", WaitTimeMs = 5600,
                BlockingSessionId = 86, TotalElapsedMs = 5600, OpenTranCount = 1,
                LoginName = "sky", HostName = "1.2.3.4", ProgramName = "Navicat",
                DbName = "dbpilot", SqlText = "SELECT 1", BatchSqlText = "SELECT 1",
                QueryHash = "abc123",
            },
            new() { SessionId = 9_000_000_000L, Status = "running", Command = "Query" },   // 超 int 界跳过
        };

        var list = MySqlProvider.MapActiveRequests(rows);

        var r = Assert.Single(list);
        Assert.Equal(42, r.SessionId);
        Assert.Equal("suspended", r.Status);
        Assert.Equal(86, r.BlockingSessionId);
        Assert.Equal("Waiting for row lock", r.WaitType);
        Assert.Equal("abc123", r.QueryHash);
        Assert.Equal(new DateTime(2026, 9, 7, 1, 2, 3), r.StartTimeUtc);
    }

    [Theory]
    [InlineData(null, 0)]      // 无阻塞
    [InlineData(-2L, -2)]      // 孤儿事务（持锁会话已断）系统节点
    [InlineData(0L, 0)]
    public void MapActiveRequests_阻塞会话号空值与负值口径(long? raw, int expected)
    {
        var list = MySqlProvider.MapActiveRequests(
        [
            new MySqlProvider.ActiveRequestRaw { SessionId = 7, BlockingSessionId = raw }
        ]);

        Assert.Equal(expected, list.Single().BlockingSessionId);
    }

    [Fact]
    public void MapHeadBlockers_会话号收敛与字段透传()
    {
        var tranBegin = new DateTime(2026, 9, 7, 0, 0, 0);
        var rows = new List<MySqlProvider.HeadBlockerRaw>
        {
            new()
            {
                SessionId = 86, LoginName = "sky", HostName = "h", ProgramName = "Navicat", DbName = "dbpilot",
                OpenTranCount = 2, TransactionBeginUtc = tranBegin, LastSqlText = "UPDATE t SET ...",
            },
            new() { SessionId = long.MaxValue },   // 超 int 界跳过
        };

        var list = MySqlProvider.MapHeadBlockers(rows);

        var r = Assert.Single(list);
        Assert.Equal(86, r.SessionId);
        Assert.Equal(2, r.OpenTranCount);
        Assert.Equal("dbpilot", r.DbName);
        Assert.Equal(tranBegin, r.TransactionBeginUtc);
        Assert.Equal("UPDATE t SET ...", r.LastSqlText);
    }

    [Fact]
    public void MapSessionLocks_会话号收敛与状态词汇()
    {
        var rows = new List<MySqlProvider.SessionLockRaw>
        {
            new() { SessionId = 42, ResourceType = "RECORD", DbName = "dbpilot", ObjectName = "t",
                    LockMode = "X", LockStatus = "WAIT" },
            new() { SessionId = 5_000_000_000L, ResourceType = "TABLE", LockMode = "IX", LockStatus = "GRANT" },   // 超界跳过
        };

        var list = MySqlProvider.MapSessionLocks(rows);

        var r = Assert.Single(list);
        Assert.Equal(42, r.SessionId);
        Assert.Equal(0, r.EntityId);           // data_locks 无数值实体 id，免反查
        Assert.Equal("WAIT", r.LockStatus);
        Assert.Equal("RECORD", r.ResourceType);
    }

    // ---- B6 Top SQL / 索引使用率 ----

    [Fact]
    public void BuildTopSqlSql_骨架_含标记与噪音排除()
    {
        var sql = MySqlProvider.BuildTopSqlSql(new Core.TopSql.TopSqlFilter());

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("events_statements_summary_by_digest", sql);
        Assert.Contains("LOWER(d.DIGEST)", sql);                          // 指纹 = DIGEST（小写归一）
        Assert.Contains("d.SUM_TIMER_WAIT DIV 1000", sql);                // ps → µs
        Assert.Contains("d.DIGEST IS NOT NULL", sql);                     // ① max_digest_length=0 时全 NULL，返回空集
        Assert.Contains("NOT LIKE '%/* dbpilot */%'", sql);               // ② 平台自监控排除
        Assert.Contains("NOT IN ('performance_schema', 'sys', 'information_schema')", sql);  // ④ 全部库排插桩内部
        Assert.Contains("OR q.DbName <> 'mysql')", sql);                  // ⑦ ExcludeSystemDb 兜底
        Assert.Contains("(@db = '' OR d.SCHEMA_NAME = @db)", sql);        // 库筛选参数化
    }

    [Fact]
    public void BuildTopSqlSql_特征模式_转义进子句_空白忽略_限量()
    {
        var patterns = new List<string> { "%`dbpilot_%", "it's", "  ", "" }
            .Concat(Enumerable.Repeat("%x%", 150))
            .ToList();
        var sql = MySqlProvider.BuildTopSqlSql(new Core.TopSql.TopSqlFilter { Patterns = patterns });

        Assert.Contains("AND d.DIGEST_TEXT NOT LIKE '%`dbpilot_%'", sql);  // 反引号模式进子句
        Assert.Contains("NOT LIKE 'it''s'", sql);                            // 单引号转义（防注入）
        Assert.DoesNotContain("LIKE '  '", sql);                             // 空白项忽略
        Assert.Equal(100, Regex.Matches(sql, "d\\.DIGEST_TEXT NOT LIKE").Count);  // 限量 MaxPatterns
    }

    [Fact]
    public void DefaultPatterns_含驱动与会话管理噪音兜底()
    {
        // mysql 冒烟实测上榜的驱动/会话管理噪音（SHOW SLAVE STATUS 等复制巡检）
        foreach (var p in new[] { "%SHOW SLAVE STATUS%", "%SET NAMES%", "%SET `AUTOCOMMIT`%",
                                  "%SET SESSION TRANSACTION ISOLATION LEVEL%", "%START TRANSACTION%",
                                  "%SHOW WARNINGS%", "%SELECT @@`IDENTITY`%", "%auto_increment_increment%" })
            Assert.Contains(p, Core.TopSql.TopSqlExcludeOptions.DefaultPatterns);
    }

    [Fact]
    public void BuildTopSqlSql_指纹黑名单_合法hex进列表_非法忽略()
    {
        var sql = MySqlProvider.BuildTopSqlSql(new Core.TopSql.TopSqlFilter
        {
            Fingerprints = ["abc123", "ZZZ-not-hex", "deadbeef"],
        });

        Assert.Contains("LOWER(d.DIGEST) NOT IN ('abc123', 'deadbeef')", sql);
        Assert.DoesNotContain("ZZZ-not-hex", sql);
    }

    [Fact]
    public void IndexUsageSql_骨架_含标记与数据源()
    {
        var sql = MySqlProvider.IndexUsageSql;

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("table_io_waits_summary_by_index_usage", sql);
        Assert.Contains("information_schema.STATISTICS", sql);            // 键列/唯一性来源
        Assert.Contains("GROUP_CONCAT", sql);
        Assert.Contains("t.INDEX_NAME IS NOT NULL", sql);                 // 跳过表级写聚合行
        Assert.Contains("t.OBJECT_SCHEMA = @db", sql);
    }

    [Fact]
    public void MapIndexUsage_字段映射与表级聚合行跳过()
    {
        var rows = new List<MySqlProvider.IndexUsageRaw>
        {
            new()   // PRIMARY：聚类 + 键列 + fetch 记 UserSeeks
            {
                DbName = "dbpilot", TableName = "orders", IndexName = "PRIMARY",
                NonUnique = 0, KeyColumns = "id",
                CountFetch = 32, CountInsert = 0, CountUpdate = 5, CountDelete = 1,
            },
            new()   // 二级索引：非唯一、无读有写
            {
                DbName = "dbpilot", TableName = "orders", IndexName = "ix_cust",
                NonUnique = 1, KeyColumns = "customer_id",
                CountFetch = 0, CountInsert = 6, CountUpdate = 2, CountDelete = 0,
            },
            new()   // 表级写聚合行（INDEX_NAME=NULL）→ 跳过
            {
                DbName = "dbpilot", TableName = "orders", IndexName = null,
                CountInsert = 6,
            },
        };

        var list = MySqlProvider.MapIndexUsage(rows);

        Assert.Equal(2, list.Count);

        var pk = list[0];
        Assert.Equal("`dbpilot`.`orders`", pk.TableName);                 // 反引号全限定（脚本可执行口径）
        Assert.True(pk.IsPrimaryKey);
        Assert.True(pk.IsUnique);
        Assert.Equal("CLUSTERED", pk.TypeDesc);
        Assert.Equal("id", pk.KeyColumns);
        Assert.Equal(32, pk.UserSeeks);
        Assert.Equal(0, pk.UserScans);                                    // MySQL 无法拆分 seek/scan/lookup
        Assert.Equal(20, pk.UserUpdates);                                 // 表级写计数（0+5+1 + 6+2+0 + 6 含 NULL 聚合行）

        var ix = list[1];
        Assert.False(ix.IsPrimaryKey);
        Assert.False(ix.IsUnique);
        Assert.Equal("NONCLUSTERED", ix.TypeDesc);
        Assert.Equal(20, ix.UserUpdates);                                 // 索引行自身无维护计数（upd 只记 PRIMARY）→ 同表级值
        Assert.Null(ix.UsedPageCount);                                    // 无每索引页数 → null（大小列空、IsUnused 不排除）
        Assert.Null(ix.LastUserSeek);
    }

    // ---- WaitBuckets：MySQL 会话等待状态词汇 ----

    [Theory]
    [InlineData("Waiting for row lock", "lock")]                    // data_lock_waits 派生
    [InlineData("Waiting for table metadata lock", "lock")]         // MDL 等待
    [InlineData("Waiting for table level lock", "lock")]
    [InlineData("Waiting for table flush", "other")]                // 非 lock 词尾 → 其他
    [InlineData("waiting for row lock", "lock")]                    // 大小写不敏感
    public void WaitBuckets_MySQL等待状态分桶(string waitType, string expectedBucket)
        => Assert.Equal(expectedBucket, WaitBuckets.BucketOf("suspended", waitType));

    [Theory]
    [InlineData("LCK_M_X", "lock")]
    [InlineData("PAGEIOLATCH_SH", "userIo")]
    public void WaitBuckets_SQLServer词汇不受影响(string waitType, string expectedBucket)
        => Assert.Equal(expectedBucket, WaitBuckets.BucketOf("suspended", waitType));

    // ---- B7 慢SQL（mysql.slow_log 表通道） ----

    [Fact]
    public void BuildSlowLogSql_无水位_不含游标谓词()
    {
        var sql = MySqlProvider.BuildSlowLogSql(hasWatermark: false);

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("FROM mysql.slow_log", sql);
        Assert.Contains("TIMESTAMPDIFF(SECOND, NOW(), UTC_TIMESTAMP())", sql);   // start_time 本地时间 → UTC
        Assert.Contains("DATE_FORMAT(start_time, '%Y-%m-%d %H:%i:%s.%f')", sql); // µs 精度水位
        Assert.Contains("TIME_TO_SEC(query_time) * 1000 + MICROSECOND(query_time) DIV 1000", sql);  // query_time → ms
        Assert.Contains("CONVERT(sql_text USING utf8mb4)", sql);                 // mediumblob → 字符串（WHERE/SELECT 双侧）
        Assert.Contains("NOT LIKE '%/* dbpilot */%'", sql);                      // ① 平台自监控排除
        Assert.Contains("SUBSTRING_INDEX(user_host, '[', 1) NOT IN ('rdsadmin', 'aurora', 'mysql.sys', 'mysql.session', 'mysql.infoschema', 'replicator')", sql);  // ② RDS 内部账号（replicator=Binlog Dump 复制线程）
        Assert.Contains("NOT LIKE '%`dbpilot_%'", sql);                            // ②b Chloe 平台写库（(My,My) 同机形态）
        Assert.Contains("LIMIT 2000", sql);                                      // ③ 单轮上限
        Assert.DoesNotContain("@wm", sql);                                       // 首扫全量
    }

    [Fact]
    public void BuildSlowLogSql_有水位_严格大于参数化()
    {
        var sql = MySqlProvider.BuildSlowLogSql(hasWatermark: true);

        Assert.Contains("AND start_time > @wm", sql);
        Assert.Contains("ORDER BY start_time", sql);                             // 水位取末行，须按 start_time 排序
    }

    [Fact]
    public void MapSlowLog_字段映射与指纹截断()
    {
        var longText = new string('a', SlowSqlEventParser.MaxTextLength + 10);
        var rows = new List<MySqlProvider.SlowLogRaw>
        {
            new()
            {
                EventTimeUtc = new DateTime(2026, 9, 7, 1, 2, 3),
                Watermark = "2026-09-07 09:02:03.123456",
                DbName = "dbpilot",
                SqlText = "SELECT * FROM orders WHERE id = 42",
                UserHost = "sky[10.0.0.1] @  proxy [10.0.0.2]",
                ThreadId = 86,
                DurationMs = 2345,
                RowsExamined = 100,
                RowsSent = 10,
            },
            new()   // 超长文本截断到 64KB
            {
                EventTimeUtc = new DateTime(2026, 9, 7, 1, 2, 4),
                Watermark = "2026-09-07 09:02:04.000000",
                SqlText = longText,
                UserHost = "root[localhost]",
                ThreadId = 87,
                DurationMs = 1000,
            },
            new()   // 空文本跳过（无指纹价值）
            {
                EventTimeUtc = new DateTime(2026, 9, 7, 1, 2, 5),
                Watermark = "2026-09-07 09:02:05.000000",
                SqlText = "   ",
                ThreadId = 88,
                DurationMs = 1500,
            },
        };

        var list = MySqlProvider.MapSlowLog(rows);

        Assert.Equal(2, list.Count);

        var r = list[0];
        Assert.Equal(DateTimeKind.Utc, r.EventTimeUtc.Kind);                     // SpecifyKind（落库/判重口径）
        Assert.Equal(new DateTime(2026, 9, 7, 1, 2, 3), r.EventTimeUtc);
        Assert.Equal("dbpilot", r.DbName);
        Assert.Equal("sky", r.LoginName);                                        // user_host '[ ' 前账号
        Assert.Equal("10.0.0.1", r.HostName);                                    // 首对 [] 内主机
        Assert.Null(r.AppName);                                                  // slow_log 无程序名列，自然降级
        Assert.Equal(86, r.SessionId);
        Assert.Equal(2, r.SqlType);                                              // 对齐 batch 口径
        Assert.Equal(2345, r.DurationMs);
        Assert.Equal(100, r.LogicalReads);                                       // rows_examined 近似逻辑读
        Assert.Equal(10, r.RowCount);                                            // rows_sent
        Assert.Null(r.CpuMs);                                                    // 无对等
        Assert.Equal(16, r.Fingerprint.Length);                                  // 归一化指纹前 16 hex
        Assert.Equal(SlowSqlEventParser.FingerprintOf("SELECT * FROM orders WHERE id = 42"), r.Fingerprint);

        Assert.Equal(SlowSqlEventParser.MaxTextLength, list[1].SqlText.Length);  // 64KB 截断
    }

    [Fact]
    public void MapSlowLog_线程号超int界收敛null()
    {
        var rows = new List<MySqlProvider.SlowLogRaw>
        {
            new() { EventTimeUtc = DateTime.UtcNow, Watermark = "w", SqlText = "SELECT 1",
                    ThreadId = 9_000_000_000L, DurationMs = 100 },
        };

        var r = MySqlProvider.MapSlowLog(rows).Single();
        Assert.Null(r.SessionId);                                                // 超 int 界不硬转（防溢出负数）
    }

    [Theory]
    [InlineData("sky[10.0.0.1] @ proxy [10.0.0.2]", "sky", "10.0.0.1")]     // 标准形态
    [InlineData("root[localhost]", "root", "localhost")]                    // 无代理段
    [InlineData("plainuser", "plainuser", null)]                            // 无 []：整体当账号
    [InlineData("", null, null)]                                             // 空 → 双 null
    [InlineData(null, null, null)]
    [InlineData("weird[unclosed", "weird", null)]                           // '[' 无 ']'：账号取前段
    public void ParseUserHost_各形态解析(string? userHost, string? expectedLogin, string? expectedHost)
    {
        var (login, host) = MySqlProvider.ParseUserHost(userHost);
        Assert.Equal(expectedLogin, login);
        Assert.Equal(expectedHost, host);
    }
}

public class MySqlParamSelfCheckTests
{
    [Fact]
    public void 尺寸参数全官方默认_无告警()
    {
        var vars = new Dictionary<string, string>
        {
            ["performance_schema_max_digest_length"] = "1024",
            ["performance_schema_max_sql_text_length"] = "1024",
            ["performance_schema_events_statements_history_size"] = "10",
            ["performance_schema_max_statement_stack"] = "10",
        };
        Assert.Empty(MySqlProvider.EvaluatePsSizing(vars));
    }

    [Fact]
    public void RDS批量置零置一_四条全告警且带参数组修复指引()
    {
        var vars = new Dictionary<string, string>
        {
            ["performance_schema_max_digest_length"] = "0",
            ["performance_schema_max_sql_text_length"] = "0",
            ["performance_schema_events_statements_history_size"] = "0",
            ["performance_schema_max_statement_stack"] = "1",
        };
        var warns = MySqlProvider.EvaluatePsSizing(vars);
        Assert.Equal(4, warns.Count);
        Assert.All(warns, w => Assert.Contains("参数组", w.FixScript));
        Assert.Contains(warns, w => w.Impact.Contains("Top SQL 榜恒空"));              // digest=0
        Assert.Contains(warns, w => w.Impact.Contains("存储过程体内语句"));            // statement_stack=1
    }

    [Fact]
    public void 键缺失_防御不告警()
        => Assert.Empty(MySqlProvider.EvaluatePsSizing(new Dictionary<string, string>()));

    [Fact]
    public void 慢日志关闭或缺TABLE_告警且为运行期修复()
    {
        var off = MySqlProvider.EvaluateSlowLogVars(new Dictionary<string, string>
        {
            ["slow_query_log"] = "OFF", ["log_output"] = "TABLE",
        });
        Assert.Single(off);
        Assert.Contains("SET GLOBAL", off[0].FixScript);

        var noTable = MySqlProvider.EvaluateSlowLogVars(new Dictionary<string, string>
        {
            ["slow_query_log"] = "ON", ["log_output"] = "FILE",
        });
        Assert.Single(noTable);
        Assert.Contains("TABLE", noTable[0].Permission);

        Assert.Empty(MySqlProvider.EvaluateSlowLogVars(new Dictionary<string, string>
        {
            ["slow_query_log"] = "ON", ["log_output"] = "FILE,TABLE",
        }));
    }
}
