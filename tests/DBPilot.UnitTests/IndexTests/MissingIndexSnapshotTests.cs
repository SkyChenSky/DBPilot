using DBPilot.Core.Indexes;
using DBPilot.Core.Providers;
using DBPilot.Storage.Entities;

namespace DBPilot.UnitTests.IndexTests;

/// <summary>缺失索引快照差集（近 7 天新增）纯函数测试。</summary>
public class MissingIndexSnapshotTests
{
    private static DbpilotMissingIndexSnapshot Row(DateTime snap, string table = "[dbo].[Orders]",
        string db = "sales", string eq = "[CustomerId]", string ineq = "[OrderDate]", string inc = "[Amount]")
        => new()
        {
            InstanceId = 1,
            DbName = db,
            TableName = table,
            EqualityColumns = eq,
            InequalityColumns = ineq,
            IncludedColumns = inc,
            UserSeeks = 100,
            AvgTotalUserCost = 1.5,
            AvgUserImpact = 90,
            Score = 135,
            CreateIndexSql = "CREATE INDEX ...",
            SnapshotTime = snap,
        };

    [Fact]
    public void BuildSnapshotResult_无历史快照_全部为新增()
    {
        var now = DateTime.UtcNow;
        var result = IndexDiagnoseService.BuildSnapshotResult([Row(now), Row(now, table: "[dbo].[Users]")]);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.True(i.IsNew));
        Assert.Equal(now, result.SnapshotTimeUtc);
        Assert.Equal(2, result.Overview.Total);
    }

    [Fact]
    public void BuildSnapshotResult_七天前已有同组合_不算新增()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-10);   // 7 天 cutoff 之外的历史
        var rows = new List<DbpilotMissingIndexSnapshot> { Row(old), Row(now), Row(now, table: "[dbo].[Users]") };

        var result = IndexDiagnoseService.BuildSnapshotResult(rows);

        // Orders 七天前就有 → 非新增；Users 是新的
        Assert.False(result.Items.Single(i => i.TableName == "[dbo].[Orders]").IsNew);
        Assert.True(result.Items.Single(i => i.TableName == "[dbo].[Users]").IsNew);
    }

    [Fact]
    public void BuildSnapshotResult_七天内的中间快照_不作为基线()
    {
        // 差集基线 = 7 天前的历史；3 天前的快照还在窗口内，不算"已存在"
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotMissingIndexSnapshot> { Row(now.AddDays(-3)), Row(now) };

        var result = IndexDiagnoseService.BuildSnapshotResult(rows);

        Assert.All(result.Items, i => Assert.True(i.IsNew));
        Assert.Single(result.Items);   // 只返回最新一批
    }

    [Fact]
    public void BuildSnapshotResult_组合键大小写与空白不敏感()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-10);
        // 七天前：同组合但列名大小写不同、带空格
        var rows = new List<DbpilotMissingIndexSnapshot>
        {
            Row(old, eq: " [customerId] ", ineq: "[orderdate]", inc: " [Amount] "),
            Row(now),
        };

        var result = IndexDiagnoseService.BuildSnapshotResult(rows);

        Assert.False(result.Items.Single().IsNew);
    }

    [Fact]
    public void BuildSnapshotResult_评分降序_时间转UTC()
    {
        var now = DateTime.UtcNow;
        var low = Row(now);
        low.Score = 10;
        var high = Row(now, table: "[dbo].[Users]");
        high.Score = 99;
        high.LastUserSeek = now;
        var rows = new List<DbpilotMissingIndexSnapshot> { low, high };

        var result = IndexDiagnoseService.BuildSnapshotResult(rows);

        Assert.Equal("[dbo].[Users]", result.Items[0].TableName);
        Assert.Equal(DateTimeKind.Utc, result.Items[0].LastUserSeek!.Value.Kind);
    }

    [Fact]
    public void BuildSnapshotResult_空表_返回空结果()
    {
        var result = IndexDiagnoseService.BuildSnapshotResult([]);
        Assert.Empty(result.Items);
        Assert.Null(result.SnapshotTimeUtc);
    }

    [Fact]
    public void BuildTrend_按批次计数_时间升序()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotMissingIndexSnapshot>
        {
            Row(now), Row(now, table: "[dbo].[Users]"),   // 最新批 2 条
            Row(now.AddDays(-1)),                          // 昨天批 1 条
        };

        var trend = IndexDiagnoseService.BuildTrend(rows);

        Assert.Equal(2, trend.Count);
        Assert.Equal(now.AddDays(-1), trend[0].SnapshotTimeUtc);   // 升序
        Assert.Equal(1, trend[0].Count);
        Assert.Equal(2, trend[1].Count);
        Assert.Equal(DateTimeKind.Utc, trend[0].SnapshotTimeUtc.Kind);
    }

    private static DbpilotMissingIndexSnapshot EmptyMarker(DateTime snap) => new()
    {
        InstanceId = 1,
        DbName = "",
        TableName = IndexDiagnoseService.EmptyBatchMarker,
        CreateIndexSql = IndexDiagnoseService.EmptyBatchMarker,
        SnapshotTime = snap,
    };

    [Fact]
    public void BuildSnapshotResult_最新批为空批次标记_条目清零且时间推进()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotMissingIndexSnapshot>
        {
            Row(now.AddDays(-1)),   // 昨天有 1 条建议
            EmptyMarker(now),       // 今天采集 0 条（用户已建索引）
        };

        var result = IndexDiagnoseService.BuildSnapshotResult(rows);

        Assert.Empty(result.Items);                     // 不再显示昨天的旧建议
        Assert.Equal(now, result.SnapshotTimeUtc);      // 上次采集时间推进到空批次
        Assert.Equal(0, result.Overview.Total);
    }

    [Fact]
    public void BuildTrend_空批次计数为零()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotMissingIndexSnapshot> { Row(now.AddDays(-1)), EmptyMarker(now) };

        var trend = IndexDiagnoseService.BuildTrend(rows);

        Assert.Equal(2, trend.Count);           // 空批次也有趋势点
        Assert.Equal(1, trend[0].Count);
        Assert.Equal(0, trend[1].Count);        // 但计数为 0
    }

    [Fact]
    public void InaccessibleDbs_Create_含库名与修复指引()
    {
        var msg = DbpilotInaccessibleDbsException.Create("缺失索引", ["dbA", "dbB"]).Message;

        Assert.Contains("dbA、dbB", msg);
        Assert.Contains("缺失索引采集", msg);
        Assert.Contains("用户映射或库内权限不足", msg);           // 文案覆盖两类根因
        Assert.Contains("CREATE USER", msg);                     // 修复指引核心动作
        Assert.Contains("VIEW DATABASE STATE", msg);             // 碎片扫描所需权限
    }
}
