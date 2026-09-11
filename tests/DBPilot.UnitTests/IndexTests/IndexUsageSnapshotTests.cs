using DBPilot.Core.Indexes;
using DBPilot.Storage.Entities;

namespace DBPilot.UnitTests.IndexTests;

/// <summary>索引使用率快照（合并口径）纯函数测试。</summary>
public class IndexUsageSnapshotTests
{
    private static DbpilotIndexUsageSnapshot Row(DateTime snap, string table = "[dbo].[Orders]",
        string index = "IX_Orders_CustomerId", string db = "sales",
        long seeks = 100, long scans = 0, long lookups = 0, long updates = 50,
        long? pages = 5000, double? frag = null, bool unused = false) => new()
    {
        InstanceId = 1,
        DbName = db,
        TableName = table,
        IndexName = index,
        IsPrimaryKey = false,
        IsUnique = false,
        TypeDesc = "NONCLUSTERED",
        UserSeeks = seeks,
        UserScans = scans,
        UserLookups = lookups,
        UserUpdates = updates,
        KeyColumns = "CustomerId",
        UsedPageCount = pages,
        IsUnused = unused,
        AvgFragmentationPercent = frag,
        SnapshotTime = snap,
    };

    // ---------------- BuildUsageSnapshotResult ----------------

    [Fact]
    public void BuildUsageSnapshotResult_排除空批次标记行()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotIndexUsageSnapshot>
        {
            Row(now),
            new()
            {
                InstanceId = 1, DbName = "", TableName = IndexDiagnoseService.EmptyBatchMarker,
                IndexName = "", TypeDesc = "", SnapshotTime = now,
            },
        };

        var result = IndexDiagnoseService.BuildUsageSnapshotResult(rows, onlineSupported: false);

        Assert.Single(result.Items);
        Assert.Equal(now, result.SnapshotTimeUtc);   // 批次时间仍取标记行推进后的时间
    }

    [Fact]
    public void BuildUsageSnapshotResult_is_unused透传_并生成禁用脚本()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotIndexUsageSnapshot>
        {
            Row(now, unused: true),
            Row(now, index: "IX_Other", unused: false),
        };

        var result = IndexDiagnoseService.BuildUsageSnapshotResult(rows, onlineSupported: false);

        var unusedItem = result.Items.Single(i => i.IsUnused);
        Assert.Equal("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] DISABLE;", unusedItem.DisableScript);
        Assert.Null(result.Items.Single(i => !i.IsUnused).DisableScript);
    }

    [Fact]
    public void BuildUsageSnapshotResult_碎片率容忍NULL_建议为None_脚本为空()
    {
        var result = IndexDiagnoseService.BuildUsageSnapshotResult([Row(DateTime.UtcNow, frag: null)], false);

        var item = result.Items.Single();
        Assert.Null(item.AvgFragmentationPercent);
        Assert.Equal(FragAction.None, item.Action);
        Assert.Equal(string.Empty, item.FragScript);
    }

    [Theory]
    [InlineData(35, FragAction.Rebuild)]
    [InlineData(15, FragAction.Reorganize)]
    [InlineData(5, FragAction.None)]
    public void BuildUsageSnapshotResult_碎片率分档建议(double frag, FragAction expected)
    {
        var result = IndexDiagnoseService.BuildUsageSnapshotResult([Row(DateTime.UtcNow, frag: frag)], false);

        Assert.Equal(expected, result.Items.Single().Action);
    }

    [Fact]
    public void BuildUsageSnapshotResult_FragScript按Online两态()
    {
        var now = DateTime.UtcNow;
        var offline = IndexDiagnoseService.BuildUsageSnapshotResult([Row(now, frag: 35)], onlineSupported: false).Items.Single();
        var online = IndexDiagnoseService.BuildUsageSnapshotResult([Row(now, frag: 15)], onlineSupported: true).Items.Single();

        Assert.Equal("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] REBUILD;", offline.FragScript);
        Assert.Equal("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] REORGANIZE;", online.FragScript);
    }

    [Fact]
    public void BuildUsageSnapshotResult_小页数SkipSmall()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotIndexUsageSnapshot>
        {
            Row(now, index: "IX_Big", pages: 5000, frag: 40),
            Row(now, index: "IX_Small", pages: 999, frag: 40),
        };

        var result = IndexDiagnoseService.BuildUsageSnapshotResult(rows, false);

        Assert.False(result.Items.Single(i => i.IndexName == "IX_Big").SkipSmall);
        Assert.True(result.Items.Single(i => i.IndexName == "IX_Small").SkipSmall);
    }

    [Fact]
    public void BuildUsageSnapshotResult_按读次数降序_时间转UTC()
    {
        var now = DateTime.UtcNow;
        var low = Row(now, index: "IX_Low", seeks: 10);
        var high = Row(now, index: "IX_High", seeks: 9999);
        high.LastUserSeek = now;
        var rows = new List<DbpilotIndexUsageSnapshot> { low, high };

        var result = IndexDiagnoseService.BuildUsageSnapshotResult(rows, false);

        Assert.Equal("IX_High", result.Items[0].IndexName);
        Assert.Equal("IX_Low", result.Items[1].IndexName);
        Assert.Equal(DateTimeKind.Utc, result.Items[0].LastUserSeek!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, result.SnapshotTimeUtc!.Value.Kind);
    }

    [Fact]
    public void BuildUsageSnapshotResult_空输入_返回空结果()
    {
        var result = IndexDiagnoseService.BuildUsageSnapshotResult([], false);

        Assert.Empty(result.Items);
        Assert.Null(result.SnapshotTimeUtc);
    }

    [Fact]
    public void BuildUsageSnapshotResult_MySQL禁用脚本INVISIBLE_默认引擎仍是DISABLE()
    {
        var now = DateTime.UtcNow;

        var my = IndexDiagnoseService.BuildUsageSnapshotResult(
            [Row(now, table: "`sales`.`orders`", unused: true)], false, engine: "mysql").Items.Single();
        Assert.Equal("ALTER TABLE `sales`.`orders` ALTER INDEX `IX_Orders_CustomerId` INVISIBLE;", my.DisableScript);

        // 默认参数（SQL Server）行为不变
        var ss = IndexDiagnoseService.BuildUsageSnapshotResult([Row(now, unused: true)], false).Items.Single();
        Assert.Equal("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] DISABLE;", ss.DisableScript);
    }

    [Fact]
    public void BuildUsageSnapshotResult_页数未知不算小表_SkipSmall()
    {
        var now = DateTime.UtcNow;
        var rows = new List<DbpilotIndexUsageSnapshot>
        {
            Row(now, index: "IX_UnknownPages", pages: null),   // MySQL 无每索引页数
        };

        var result = IndexDiagnoseService.BuildUsageSnapshotResult(rows, false, engine: "mysql");

        Assert.False(result.Items.Single().SkipSmall);   // null ≠ 小表，不误导"小表不建议"
    }

    // ---------------- BuildUsageOverview ----------------

    [Fact]
    public void BuildUsageOverview_计数与总空间()
    {
        var items = new List<IndexUsageSnapshotItem>
        {
            new() { UsedPageCount = 1024, AvgFragmentationPercent = 35, UserSeeks = 50, UserUpdates = 0 },
            new() { UsedPageCount = 2048, AvgFragmentationPercent = 5, UserSeeks = 500, UserUpdates = 10 },
            new() { UsedPageCount = null, AvgFragmentationPercent = null, UserSeeks = 0, UserUpdates = 0 },
        };

        var o = IndexDiagnoseService.BuildUsageOverview(items);

        Assert.Equal(3, o.Total);
        Assert.Equal(3072, o.TotalPages);
        Assert.Equal(24, o.TotalSpaceMb);            // 3072 × 8 / 1024
        Assert.Equal(1, o.FragOver30Count);          // 只有 35% 一条
        Assert.Equal(2, o.LowReadCount);             // 50 与 0 两条 < 100
        Assert.Equal(0, o.LowReadRatioCount);        // 读占比都不低
    }

    [Fact]
    public void BuildUsageOverview_读占比低计入_分母为零不计()
    {
        var items = new List<IndexUsageSnapshotItem>
        {
            new() { UserSeeks = 5, UserUpdates = 1000 },    // 5/1005 < 10% → 计入
            new() { UserSeeks = 200, UserUpdates = 100 },   // 200/300 ≥ 10% → 不计
            new() { UserSeeks = 0, UserUpdates = 0 },       // 分母 0 → 不计
        };

        var o = IndexDiagnoseService.BuildUsageOverview(items);

        Assert.Equal(1, o.LowReadRatioCount);
        Assert.Equal(2, o.LowReadCount);
    }

    [Fact]
    public void BuildUsageOverview_空列表()
    {
        var o = IndexDiagnoseService.BuildUsageOverview([]);

        Assert.Equal(0, o.Total);
        Assert.Equal(0, o.TotalPages);
        Assert.Equal(0, o.TotalSpaceMb);
    }

    // ---------------- BuildUsageTrend ----------------

    [Fact]
    public void BuildUsageTrend_升序与求和()
    {
        var now = DateTime.UtcNow;
        var rows = new List<IndexDiagnoseService.UsageTrendRow>
        {
            new() { SnapshotTime = now, TotalPages = 3000 },
            new() { SnapshotTime = now.AddDays(-1), TotalPages = 1000 },
        };

        var trend = IndexDiagnoseService.BuildUsageTrend(rows);

        Assert.Equal(2, trend.Count);
        Assert.Equal(now.AddDays(-1), trend[0].SnapshotTimeUtc);   // 升序
        Assert.Equal(1000, trend[0].TotalPages);
        Assert.Equal(DateTimeKind.Utc, trend[0].SnapshotTimeUtc.Kind);
    }

    [Fact]
    public void BuildUsageTrend_空批次点保留为零()
    {
        var now = DateTime.UtcNow;
        var rows = new List<IndexDiagnoseService.UsageTrendRow>
        {
            new() { SnapshotTime = now.AddDays(-1), TotalPages = 1000 },
            new() { SnapshotTime = now, TotalPages = null },       // 空批次（SUM 为 NULL）
        };

        var trend = IndexDiagnoseService.BuildUsageTrend(rows);

        Assert.Equal(2, trend.Count);
        Assert.Equal(0, trend[1].TotalPages);
    }
}
