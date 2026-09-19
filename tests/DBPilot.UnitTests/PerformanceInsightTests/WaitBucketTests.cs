using DBPilot.Core.Blocking;
using DBPilot.Core.PerformanceInsight;

namespace DBPilot.UnitTests.PerformanceInsightTests;

/// <summary>等待分桶映射：前缀族 / 未命中归其他 / 无用等待排除。</summary>
public class WaitBucketTests
{
    [Theory]
    [InlineData(null, "cpu")]                    // running 无等待
    [InlineData("", "cpu")]
    [InlineData("SOS_SCHEDULER_YIELD", "cpu")]
    [InlineData("PAGEIOLATCH_SH", "userIo")]
    [InlineData("PAGEIOLATCH_EX", "userIo")]
    [InlineData("DISKIO", "userIo")]
    [InlineData("WRITELOG", "logWrite")]
    [InlineData("LOGBUFFER", "logWrite")]
    [InlineData("LOGMGR_FLUSH", "logWrite")]
    [InlineData("IO_COMPLETION", "sysIo")]
    [InlineData("LCK_M_X", "lock")]
    [InlineData("LCK_M_S", "lock")]
    [InlineData("PAGELATCH_EX", "bufferLatch")]
    [InlineData("LATCH_EX", "latch")]
    [InlineData("ACCESS_METHODS_DATASET_PARENT", "latch")]
    [InlineData("ASYNC_NETWORK_IO", "network")]
    [InlineData("RESOURCE_SEMAPHORE", "memory")]
    [InlineData("CMEMTHREAD", "memory")]
    [InlineData("CXPACKET", "parallel")]
    [InlineData("CXCONSUMER", "parallel")]
    [InlineData("THREADPOOL", "threads")]        // 工作线程耗尽
    [InlineData("BACKUPIO", "backup")]
    [InlineData("BACKUPBUFFER", "backup")]
    [InlineData("HADR_LOGCAPTURE_WAIT", "hadr")]
    [InlineData("DBMIRROR_SEND", "hadr")]
    [InlineData("REPL_SCHEMA_VERSION", "hadr")]
    [InlineData("XE_LIVE_TARGET_TVF", "trace")]  // 此前误入"其他"，现归追踪桶
    [InlineData("SQLTRACE_LOCK", "trace")]
    [InlineData("QDS_SHUTDOWN_QUEUE", "trace")]
    [InlineData("BROKER_RECEIVE_WAITFOR", "broker")]   // Broker 队列等待，非 WAITFOR 空闲
    [InlineData("MSSEARCH", "fullText")]
    [InlineData("FULLTEXT_GATHERER", "fullText")]
    [InlineData("PREEMPTIVE_OS_CRYPTOPS", "preemptive")]
    [InlineData("SOME_UNKNOWN_WAIT", "other")]   // 未命中禁止丢弃（R3）
    [InlineData("WAITFOR", "userWait")]          // 用户显式延迟 → userWait 桶（计活跃，会话占用口径）
    [InlineData("SLEEP", "idle")]                // 引擎后台空闲 → idle 桶（保留分类，不计 AAS）
    [InlineData("BROKER_TASK_WAIT", "idle")]
    [InlineData("DIRTY_PAGE_POLL", "idle")]
    public void 桶映射_按等待类型(string? waitType, string? expected)
    {
        Assert.Equal(expected, WaitBuckets.BucketOf("suspended", waitType));
    }

    [Fact]
    public void 桶清单_二十桶且顺序固定()
    {
        Assert.Equal(20, WaitBuckets.All.Length);
        Assert.Equal("other", WaitBuckets.All[^1]);
        Assert.Equal("idle", WaitBuckets.All[^2]);
        Assert.Equal("userWait", WaitBuckets.All[^3]);
        Assert.Equal(20, WaitBuckets.DisplayNames.Count);
        Assert.Equal(WaitBuckets.All.Length, WaitBuckets.All.Distinct().Count());   // 无重复
    }
}

/// <summary>单次采样聚合（SampleTickBuilder）：活跃过滤 / 桶计数 / 7 维计数 / SQL 指纹兜底。</summary>
public class SampleTickBuilderTests
{
    private static readonly DateTime T = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ActiveRequestRow Row(string status, string? waitType, string? sql = null, string? login = "app", string? db = "db1") => new()
    {
        SessionId = 60,
        Status = status,
        WaitType = waitType,
        Command = "SELECT",
        LoginName = login,
        HostName = "h1",
        DbName = db,
        SqlText = sql,
    };

    [Fact]
    public void 活跃过滤_非活跃状态与无用等待不计()
    {
        var tick = SampleTickBuilder.Build(
        [
            Row("running", null),            // 活跃 CPU
            Row("suspended", "LCK_M_X"),     // 活跃 锁
            Row("suspended", "WAITFOR"),     // 用户等待 → 计活跃、归 userWait 桶（会话占用口径）
            Row("suspended", "SLEEP"),       // 引擎后台空闲 → idle 桶保留分类，不计 AAS
            Row("sleeping", null),           // 非活跃状态 → 排除
            Row("rollback", "LCK_M_X"),      // rollback 不计 AAS（仅供阻塞树）
        ], T);

        Assert.Equal(3, tick.ActiveCount);
        Assert.Equal(1, tick.Buckets["cpu"]);
        Assert.Equal(1, tick.Buckets["lock"]);
        Assert.Equal(1, tick.Buckets["userWait"]);
        Assert.False(tick.Buckets.ContainsKey("idle"));   // idle 不计 AAS
    }

    [Fact]
    public void 七维计数_值缺失归占位符()
    {
        var tick = SampleTickBuilder.Build([Row("running", null, login: null, db: null)], T);

        Assert.Equal(1, tick.Dims["user"]["-"]);          // login 缺失归占位符
        Assert.Equal(1, tick.Dims["db"]["-"]);
        Assert.Equal(1, tick.Dims["wait"]["(running)"]);
        Assert.Equal(8, tick.Dims.Count);                 // 七维 + sqlWait 联合维度
    }

    [Fact]
    public void sqlWait联合维度_指纹桶复合键()
    {
        var tick = SampleTickBuilder.Build(
        [
            Row("running", null, sql: "SELECT 1"),
            Row("suspended", "LCK_M_X", sql: "SELECT 2"),
        ], T);

        var entries = tick.Dims["sqlWait"];
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries.Keys, k => k.EndsWith("|cpu"));
        Assert.Contains(entries.Keys, k => k.EndsWith("|lock"));
        Assert.All(entries.Values, v => Assert.Equal(1, v));
        // 复合键前半段必须是已登记的 SQL 指纹
        foreach (var k in entries.Keys)
            Assert.Contains(k[..k.LastIndexOf('|')], tick.Dims["sql"].Keys);
    }

    [Fact]
    public void SQL指纹_queryHash优先_未命中走文本归一化()
    {
        var a = SampleTickBuilder.Build([Row("running", null, sql: "SELECT  1")], T);
        var b = SampleTickBuilder.Build([Row("running", null, sql: "select 1 ")], T);   // 大小写/空白差异归一
        var c = SampleTickBuilder.Build([Row("running", null, sql: "SELECT 2")], T);

        Assert.NotEqual("-", a.Dims["sql"].Keys.Single());
        Assert.Equal(a.Dims["sql"].Keys.Single(), b.Dims["sql"].Keys.Single());
        Assert.NotEqual(a.Dims["sql"].Keys.Single(), c.Dims["sql"].Keys.Single());

        var withHash = Row("running", null, sql: "whatever");
        withHash.QueryHash = "ABC123";
        var d = SampleTickBuilder.Build([withHash], T);
        Assert.Equal("abc123", d.Dims["sql"].Keys.Single());
    }
}

/// <summary>分钟聚合（MinuteAggregator）：AAS 均值/最大、桶与维度贡献、TopN 截断。</summary>
public class MinuteAggregatorTests
{
    private static readonly DateTime Minute = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static SampleTick MkTick(int minuteOffsetSec, int active, string bucket = "cpu", string user = "app") => new()
    {
        TimeUtc = Minute.AddSeconds(minuteOffsetSec),
        ActiveCount = active,
        Buckets = new Dictionary<string, int> { [bucket] = active },
        Dims = new Dictionary<string, Dictionary<string, int>>
        {
            ["user"] = new() { [user] = active },
        },
    };

    [Fact]
    public void 空tick_返回null()
    {
        Assert.Null(MinuteAggregator.Aggregate(Minute, []));
    }

    [Fact]
    public void AAS_均值与最大值()
    {
        var agg = MinuteAggregator.Aggregate(Minute, [MkTick(0, 2), MkTick(10, 4), MkTick(20, 6), MkTick(30, 0)])!;

        Assert.Equal(4, agg.SampleCount);
        Assert.Equal(3m, agg.AvgActive);      // (2+4+6+0)/4
        Assert.Equal(6, agg.MaxActive);
        Assert.Equal(3m, agg.Buckets["cpu"]);      // Σ桶样本 12 / 4 tick
    }

    [Fact]
    public void 维度_Top10截断_贡献为占比()
    {
        var ticks = Enumerable.Range(0, 12)
            .Select(i => MkTick(i * 5, 1, user: $"u{i}"))
            .ToList();

        var agg = MinuteAggregator.Aggregate(Minute, ticks)!;

        Assert.Equal(10, agg.Dims["user"].Count);              // 截断 Top10
        Assert.Equal(Math.Round(1 / 12m, 4), agg.Dims["user"][0].Value);  // 每用户 1 样本 / 12
    }
}

/// <summary>MinuteAggregate → 实体映射（Mapster 全局配置：异名列 + JSON 计算列）。</summary>
public class SampleFlushMappingTests
{
    [Fact]
    public void ToEntity_列映射与JSON()
    {
        var agg = new MinuteAggregate
        {
            MinuteUtc = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc),
            SampleCount = 6,
            AvgActive = 2.5m,
            MaxActive = 4,
            Buckets = new Dictionary<string, decimal> { ["cpu"] = 1.5m, ["lock"] = 1m },
            Dims = new Dictionary<string, List<(string Key, decimal Value)>>
            {
                ["user"] = [("app", 1.0m), ("web", 1.5m)],
            },
        };

        var entity = DBPilot.Core.PerformanceInsight.SampleFlushService.ToEntity(3, agg);

        Assert.Equal(3, entity.InstanceId);
        Assert.Equal(agg.MinuteUtc, entity.MinuteTime);
        Assert.Equal(6, entity.SampleCount);
        Assert.Equal(2.5m, entity.AvgActiveSessions);
        Assert.Equal(4, entity.MaxActiveSessions);
        Assert.Contains("\"cpu\":1.5", entity.Buckets);
        Assert.Contains("\"key\":\"app\",\"value\":1.0", entity.Dims);
        Assert.True(entity.CreateTime > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(0, entity.Id);   // 自增列不映射
    }

    [Fact]
    public void JSON口径统一_新写与存量格式互读()
    {
        // JSON 口径统一（SerializeExtension）回归：
        // ① 新写入（ToJson）→ 读回（ParseBuckets/ParseDims）无损；
        // ② 存量数据（旧裸 JsonConvert 写出，键本身小写 → 文本一致）读回同样无损。
        var agg = new MinuteAggregate
        {
            MinuteUtc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
            SampleCount = 2,
            AvgActive = 1m,
            MaxActive = 2,
            Buckets = new Dictionary<string, decimal> { ["cpu"] = 0.5m, ["userIo"] = 0.5m },
            Dims = new Dictionary<string, List<(string Key, decimal Value)>>
            {
                ["sql"] = [("a1b2c3d4e5f60718", 1.0m)],
            },
        };

        var entity = DBPilot.Core.PerformanceInsight.SampleFlushService.ToEntity(1, agg);

        var buckets = PerformanceInsightService.ParseBuckets(entity.Buckets);
        Assert.Equal(0.5m, buckets["cpu"]);
        Assert.Equal(0.5m, buckets["userIo"]);

        var dims = PerformanceInsightService.ParseDims(entity.Dims);
        Assert.Equal(1.0m, dims["sql"]["a1b2c3d4e5f60718"]);

        // ② 存量格式样本（旧写出文本）
        var legacyBuckets = PerformanceInsightService.ParseBuckets("""{"cpu":0.5,"userIo":0.5}""");
        Assert.Equal(0.5m, legacyBuckets["userIo"]);
        var legacyDims = PerformanceInsightService.ParseDims(
            """{"sql":[{"key":"a1b2c3d4e5f60718","value":1.0}],"sqlWait":[{"key":"a1b2c3d4e5f60718|lock","value":1.0}]}""");
        Assert.Equal(1.0m, legacyDims["sqlWait"]["a1b2c3d4e5f60718|lock"]);
    }
}

/// <summary>AAS 维度数学：TopN 均值贡献 / 单值时序空洞。</summary>
public class AasDimMathTests
{
    [Fact]
    public void TopN_均值贡献_降序截断()
    {
        var points = new List<Dictionary<string, decimal>>
        {
            new() { ["a"] = 3m, ["b"] = 1m },
            new() { ["a"] = 1m, ["c"] = 2m },
            new() { ["a"] = 2m },               // b/c 该点无样本
            new(),
        };

        var top = AasDimMath.TopN(points, 2);

        Assert.Equal(("a", 1.5m), (top[0].Key, top[0].Aas));   // (3+1+2)/4
        Assert.Equal(("c", 0.5m), (top[1].Key, top[1].Aas));   // c=2/4 > b=1/4
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Series_无样本点为null()
    {
        var points = new List<Dictionary<string, decimal>>
        {
            new() { ["a"] = 2m },
            new(),
            new() { ["a"] = 1m },
        };

        var series = AasDimMath.Series(points, "a");

        Assert.Equal([2m, null, 1m], series);
    }

    [Fact]
    public void ParseDims_JSON解析与脏数据容错()
    {
        var dims = PerformanceInsightService.ParseDims(
            """{"sql":[{"key":"abc","value":1.5},{"key":"def","value":0.25}],"user":[{"key":"app","value":2}]}""");

        Assert.Equal(1.5m, dims["sql"]["abc"]);
        Assert.Equal(0.25m, dims["sql"]["def"]);
        Assert.Equal(2m, dims["user"]["app"]);
        Assert.Empty(PerformanceInsightService.ParseDims("not-json"));
    }

    [Fact]
    public void TotalAverage_全值合计均摊()
    {
        var points = new List<Dictionary<string, decimal>>
        {
            new() { ["a"] = 3m, ["b"] = 1m },
            new() { ["a"] = 1m },
            new(),
        };

        Assert.Equal(1.6667m, AasDimMath.TotalAverage(points));   // (3+1+1)/3
        Assert.Equal(0m, AasDimMath.TotalAverage([]));
    }
}

/// <summary>SQL 文本字典（内存）：首见优先 / 占位符排除。</summary>
public class SqlTemplateDictionaryTests
{
    [Fact]
    public void 空缓冲_LastTickUtc为null_不抛Max异常()
    {
        var buffer = new InstanceSampleBuffer();
        Assert.Null(buffer.LastTickUtc);   // 启动后尚无 tick（曾抛 Sequence contains no elements）
        buffer.Add(new SampleTick { TimeUtc = new DateTime(2026, 8, 25, 2, 41, 50, DateTimeKind.Utc) });
        Assert.Equal(new DateTime(2026, 8, 25, 2, 41, 50, DateTimeKind.Utc), buffer.LastTickUtc);
    }

    [Fact]
    public void 首见文本优先_空文本跳过()
    {
        var buffer = new InstanceSampleBuffer();
        var mk = (string? sql, string? hash = null) => new DBPilot.Core.Blocking.ActiveRequestRow
        {
            SessionId = 1, Status = "running", SqlText = sql, QueryHash = hash,
        };

        buffer.RememberSqlTexts([mk("SELECT first", "ABC"), mk(null)]);
        buffer.RememberSqlTexts([mk("SELECT second", "abc")]);     // 同指纹（大小写归一）→ 首见保留

        Assert.Single(buffer.SqlTemplates);
        Assert.Equal("SELECT first", buffer.SqlTemplates["abc"].SqlText);

        // 语句级文本缺失（offset 截取 NULL）→ 兜底整批文本 BatchSqlText
        var batchOnly = mk(null, "DEF");
        batchOnly.BatchSqlText = "BEGIN SELECT 1 END";
        buffer.RememberSqlTexts([batchOnly]);
        Assert.Equal("BEGIN SELECT 1 END", buffer.SqlTemplates["def"].SqlText);
    }
}

/// <summary>Load By SQL 噪音过滤：黑名单 / RDS 与平台标记 / LIKE 模式（对齐 Top SQL 排除规则）。</summary>
public class SqlNoiseFilterTests
{
    private static readonly HashSet<string> Blacklist = ["deadbeef00000000"];

    [Fact]
    public void 黑名单命中_文本无关()
    {
        Assert.True(SqlNoiseFilter.IsExcluded("deadbeef00000000", null, Blacklist, []));
        Assert.False(SqlNoiseFilter.IsExcluded("abcd000000000000", null, Blacklist, []));   // 无文本不误杀
    }

    [Fact]
    public void RDS与平台标记前缀_忽略大小写()
    {
        Assert.True(SqlNoiseFilter.IsExcluded("a1", "/* rds internal mark */ SELECT 1", [], []));
        Assert.True(SqlNoiseFilter.IsExcluded("a2", "  /* DBPILOT */ SELECT 1", [], []));
        Assert.False(SqlNoiseFilter.IsExcluded("a3", "SELECT /* dbpilot */ 1", [], []));    // 非前缀不杀
    }

    [Theory]
    [InlineData("%fn_MSxe_read_event_stream%", "select * from sys.fn_MSxe_read_event_stream(@s)", true)]
    [InlineData("%FROM sys.traces%", "SELECT * FROM sys.traces", true)]
    [InlineData("%FROM sys.traces%", "SELECT * FROM sys.trace_files", false)]        // _ 通配单字符
    [InlineData("%[[]name]%", "AS [name]", true)]                                     // [[] = 字面方括号
    [InlineData("%[[]name]%", "AS name", false)]
    [InlineData("%sp_sqlagent[_]%", "EXEC sp_sqlagent_get", true)]                    // [_] = 字面下划线
    [InlineData("%sp_sqlagent[_]%", "EXEC sp_sqlagentX", false)]
    public void LIKE模式_语义对齐(string pattern, string sql, bool expected)
    {
        Assert.Equal(expected, SqlNoiseFilter.IsExcluded("fp", sql, [], [pattern]));
    }
}
