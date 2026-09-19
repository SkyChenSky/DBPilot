using DBPilot.Core.Blocking;

namespace DBPilot.UnitTests.BlockTests;

/// <summary>阻塞趋势时间桶聚合：桶粒度自适应 / 桶起点对齐 / 空桶补零 / 范围外丢弃。</summary>
public class BlockingTrendTests
{
    private static readonly DateTime From = new(2026, 8, 28, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(2);

    [Fact]
    public void 跨度两小时内_桶粒度为5分钟()
    {
        var t = BlockingService.Bucketize([], From, To);
        Assert.Equal(5, t.StepMinutes);
        Assert.Equal(24, t.Items.Count);                       // 2h / 5min = 24 桶
        Assert.All(t.Items, i => Assert.Equal(0, i.Count));    // 无事件 → 全零桶
    }

    [Fact]
    public void 跨度24小时_桶粒度为15分钟()
    {
        var t = BlockingService.Bucketize([], From, From.AddHours(24));
        Assert.Equal(15, t.StepMinutes);
        Assert.Equal(96, t.Items.Count);
    }

    [Fact]
    public void 跨度7天_桶粒度为2小时()
    {
        var t = BlockingService.Bucketize([], From, From.AddDays(7));
        Assert.Equal(120, t.StepMinutes);
        Assert.Equal(84, t.Items.Count);                       // 7d / 2h = 84 桶
    }

    [Fact]
    public void 同桶事件聚合_次数与等待秒累计()
    {
        var rows = new List<(DateTime, int)>
        {
            (From.AddMinutes(3), 10),
            (From.AddMinutes(4), 5),    // 与上一条同属 [20:00, 20:05) 桶
            (From.AddMinutes(13), 7),   // [20:10, 20:15) 桶
        };

        var t = BlockingService.Bucketize(rows, From, To);

        Assert.Equal(5, t.StepMinutes);
        var first = t.Items[0];
        Assert.Equal(From, first.BucketStartUtc);
        Assert.Equal(2, first.Count);
        Assert.Equal(15, first.TotalWaitSeconds);
        Assert.Equal(0, t.Items[1].Count);
        Assert.Equal(1, t.Items[2].Count);
        Assert.Equal(7, t.Items[2].TotalWaitSeconds);
    }

    [Fact]
    public void 桶起点对齐step_非整分from()
    {
        var from = From.AddMinutes(3).AddSeconds(30);           // 20:03:30
        var t = BlockingService.Bucketize([], from, from.AddMinutes(10));

        // 首桶起点回退对齐到 20:00（step=5min 网格），末桶覆盖 [to-步长, to)
        Assert.Equal(new DateTime(2026, 8, 28, 20, 0, 0, DateTimeKind.Utc), t.Items[0].BucketStartUtc);
        Assert.Equal(3, t.Items.Count);
    }

    [Fact]
    public void from之前的事件丢弃()
    {
        var rows = new List<(DateTime, int)> { (From.AddMinutes(-1), 10) };

        var t = BlockingService.Bucketize(rows, From, To);

        // 对齐后 key = 19:55 < from 桶集合外 → 不入桶
        Assert.All(t.Items, i => Assert.Equal(0, i.Count));
    }
}
