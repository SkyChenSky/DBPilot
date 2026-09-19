using DBPilot.Core.TopSql;

namespace DBPilot.UnitTests.TopSqlTests;

public class TopSqlServiceTests
{
    [Fact]
    public void BuildItems_微秒转毫秒与平均值()
    {
        var items = TopSqlService.BuildItems([
            new TopSqlRawRow
            {
                Fingerprint = "ab12",
                ExecutionCount = 4,
                TotalElapsedUs = 4_000_000,   // 4000ms
                TotalWorkerUs = 1_000_000,     // 1000ms
                LastExecutionTime = new DateTime(2026, 8, 21, 2, 0, 0),
            },
        ]);

        var i = items.Single();
        Assert.Equal(4000, i.TotalElapsedMs);
        Assert.Equal(1000, i.AvgElapsedMs);       // 4000 / 4
        Assert.Equal(1000, i.TotalCpuMs);
        Assert.Equal(250, i.AvgCpuMs);            // 1000 / 4
        Assert.Equal(DateTimeKind.Utc, i.LastExecutionTimeUtc!.Value.Kind);
    }

    [Fact]
    public void BuildItems_执行次数为零不除零()
    {
        var i = TopSqlService.BuildItems([
            new TopSqlRawRow { Fingerprint = "x", ExecutionCount = 0, TotalElapsedUs = 123_456 },
        ]).Single();

        Assert.Equal(123.456, i.TotalElapsedMs);
        Assert.Equal(0, i.AvgElapsedMs);
        Assert.Equal(0, i.AvgCpuMs);
    }

    [Fact]
    public void BuildItems_文本去空白并截断4000()
    {
        var longText = "  " + new string('a', 5000) + "  ";
        var i = TopSqlService.BuildItems([new TopSqlRawRow { SqlText = longText }]).Single();

        Assert.Equal(4000, i.SqlText.Length);
        Assert.StartsWith("a", i.SqlText);   // 已 Trim
    }

    [Fact]
    public void BuildItems_空文本容错()
    {
        var i = TopSqlService.BuildItems([new TopSqlRawRow()]).Single();
        Assert.Equal("", i.SqlText);
        Assert.Null(i.LastExecutionTimeUtc);
    }

    [Fact]
    public void 排序_总耗时口径降序()
    {
        var items = TopSqlService.BuildItems([
            new TopSqlRawRow { Fingerprint = "a", ExecutionCount = 100, TotalElapsedUs = 1_000 },
            new TopSqlRawRow { Fingerprint = "b", ExecutionCount = 1, TotalElapsedUs = 9_000_000 },
        ]);

        var ordered = TopSqlService.OrderByMetric(items, "total").ToList();
        Assert.Equal(["b", "a"], ordered.Select(x => x.Fingerprint));
    }

    [Fact]
    public void 排序_平均耗时口径降序()
    {
        // a：总量大但平均低；b：总量小但平均高
        var items = TopSqlService.BuildItems([
            new TopSqlRawRow { Fingerprint = "a", ExecutionCount = 100, TotalElapsedUs = 10_000_000 },  // avg 100ms
            new TopSqlRawRow { Fingerprint = "b", ExecutionCount = 1, TotalElapsedUs = 1_000_000 },     // avg 1000ms
        ]);

        var ordered = TopSqlService.OrderByMetric(items, "avg").ToList();
        Assert.Equal(["b", "a"], ordered.Select(x => x.Fingerprint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("xxx")]
    [InlineData("AVG")]   // 大小写敏感，非 "avg" 一律按 total
    public void 排序_非法口径回退总耗时(string metric)
    {
        var items = TopSqlService.BuildItems([
            new TopSqlRawRow { Fingerprint = "a", ExecutionCount = 100, TotalElapsedUs = 1_000 },
            new TopSqlRawRow { Fingerprint = "b", ExecutionCount = 1, TotalElapsedUs = 9_000_000 },
        ]);

        var ordered = TopSqlService.OrderByMetric(items, metric).ToList();
        Assert.Equal("b", ordered[0].Fingerprint);
    }

    [Fact]
    public void 百分比_各指标占合计比()
    {
        var items = TopSqlService.BuildItems([
            new TopSqlRawRow { Fingerprint = "a", ExecutionCount = 1, TotalElapsedUs = 300_000, TotalWorkerUs = 100_000, TotalLogicalReads = 300 },
            new TopSqlRawRow { Fingerprint = "b", ExecutionCount = 3, TotalElapsedUs = 100_000, TotalWorkerUs = 300_000, TotalLogicalReads = 100 },
        ]);
        TopSqlService.AttachPercents(items);

        var (a, b) = (items[0], items[1]);
        Assert.Equal(25, a.ExecutionCountPercent);      // 1/4
        Assert.Equal(75, b.ExecutionCountPercent);
        Assert.Equal(75, a.TotalElapsedPercent);        // 300/400
        Assert.Equal(25, a.TotalCpuPercent);            // 100/400
        Assert.Equal(75, a.LogicalReadsPercent);
    }

    [Fact]
    public void 百分比_空列表与零合计不除零()
    {
        var empty = new List<DBPilot.Core.TopSql.TopSqlRealtimeItem>();
        TopSqlService.AttachPercents(empty);   // 不抛异常

        var zeros = TopSqlService.BuildItems([new TopSqlRawRow { Fingerprint = "z" }]);
        TopSqlService.AttachPercents(zeros);
        Assert.Equal(0, zeros[0].ExecutionCountPercent);
        Assert.Equal(0, zeros[0].TotalElapsedPercent);
    }
}
