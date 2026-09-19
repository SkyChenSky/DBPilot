using DBPilot.Core.TopSql;

namespace DBPilot.UnitTests.TopSqlTests;

/// <summary>Top SQL 分钟差值计算（ComputeDeltas）：正常差值 / 新指纹全量 / 负差值跳过（计划驱逐）/ 零活动。</summary>
public class TopSqlDeltaTests
{
    private static readonly DateTime Start = new(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 8, 27, 1, 1, 0, DateTimeKind.Utc);

    private static TopSqlRawRow Row(string fp, string? db, long exec, long elapsedUs, long workerUs = 0,
        long logical = 0, long physical = 0, long writes = 0, long maxUs = 0) => new()
    {
        Fingerprint = fp,
        DbName = db,
        ExecutionCount = exec,
        TotalElapsedUs = elapsedUs,
        TotalWorkerUs = workerUs,
        TotalLogicalReads = logical,
        TotalPhysicalReads = physical,
        TotalWrites = writes,
        MaxElapsedUs = maxUs,
    };

    [Fact]
    public void 正常差值_相邻两拍求差()
    {
        var baseline = new Dictionary<string, TopSqlCounter>
        {
            [TopSqlDeltaCollectService.Key("db1", "aa")] = new(10, 5_000_000, 2_000_000, 100, 20, 5),
        };
        var rows = new List<TopSqlRawRow> { Row("aa", "db1", 13, 8_000_000, 3_500_000, 160, 25, 9, 900_000) };

        var (deltas, newValues) = TopSqlDeltaCollectService.ComputeDeltas(1, Start, End, baseline, rows);

        var d = deltas.Single();
        Assert.Equal(3, d.ExecCount);
        Assert.Equal(3000, d.TotalElapsedMs);
        Assert.Equal(1500, d.TotalWorkerMs);
        Assert.Equal(60, d.TotalLogicalReads);
        Assert.Equal(5, d.TotalPhysicalReads);
        Assert.Equal(4, d.TotalWrites);
        Assert.Equal(900, d.MaxElapsedMs);         // 单拍快照的 max，非差值
        Assert.Equal("db1", d.DbName);
        Assert.Equal(Start, d.WindowStart);
        Assert.Equal(End, d.WindowEnd);
        Assert.Equal(new TopSqlCounter(13, 8_000_000, 3_500_000, 160, 25, 9),
            newValues[TopSqlDeltaCollectService.Key("db1", "aa")]);
    }

    [Fact]
    public void 新指纹_落全量差值()
    {
        var rows = new List<TopSqlRawRow> { Row("new1", "db1", 7, 2_100_000) };

        var (deltas, newValues) = TopSqlDeltaCollectService.ComputeDeltas(1, Start, End,
            new Dictionary<string, TopSqlCounter>(), rows);

        var d = deltas.Single();
        Assert.Equal(7, d.ExecCount);
        Assert.Equal(2100, d.TotalElapsedMs);
        Assert.Contains(TopSqlDeltaCollectService.Key("db1", "new1"), newValues.Keys);
    }

    [Fact]
    public void 负差值_计划驱逐重建_跳过且基线重置()
    {
        var baseline = new Dictionary<string, TopSqlCounter>
        {
            [TopSqlDeltaCollectService.Key("db1", "aa")] = new(100, 50_000_000, 0, 0, 0, 0),
        };
        var rows = new List<TopSqlRawRow> { Row("aa", "db1", 2, 500_000) };   // 计数器清零后重新累计

        var (deltas, newValues) = TopSqlDeltaCollectService.ComputeDeltas(1, Start, End, baseline, rows);

        Assert.Empty(deltas);
        Assert.Equal(new TopSqlCounter(2, 500_000, 0, 0, 0, 0), newValues[TopSqlDeltaCollectService.Key("db1", "aa")]);
    }

    [Fact]
    public void 零活动_不落行()
    {
        var counter = new TopSqlCounter(10, 1_000_000, 0, 0, 0, 0);
        var baseline = new Dictionary<string, TopSqlCounter> { [TopSqlDeltaCollectService.Key(null, "aa")] = counter };
        var rows = new List<TopSqlRawRow> { Row("aa", null, 10, 1_000_000) };

        var (deltas, _) = TopSqlDeltaCollectService.ComputeDeltas(1, Start, End, baseline, rows);

        Assert.Empty(deltas);
    }

    [Fact]
    public void 同指纹不同库_独立键()
    {
        var baseline = new Dictionary<string, TopSqlCounter>
        {
            [TopSqlDeltaCollectService.Key("db1", "aa")] = new(10, 1_000_000, 0, 0, 0, 0),
            [TopSqlDeltaCollectService.Key("db2", "aa")] = new(10, 1_000_000, 0, 0, 0, 0),
        };
        var rows = new List<TopSqlRawRow>
        {
            Row("aa", "db1", 11, 1_100_000),
            Row("aa", "db2", 12, 1_200_000),
        };

        var (deltas, _) = TopSqlDeltaCollectService.ComputeDeltas(1, Start, End, baseline, rows);

        Assert.Equal(2, deltas.Count);
        Assert.Equal(1, deltas.Single(d => d.DbName == "db1").ExecCount);
        Assert.Equal(2, deltas.Single(d => d.DbName == "db2").ExecCount);
    }
}
