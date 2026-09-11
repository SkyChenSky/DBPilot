using DBPilot.Common;
using DBPilot.Core.Deadlocks;

namespace DBPilot.UnitTests.DeadlockTests;

/// <summary>死锁趋势桶粒度：聚合在 SQL 侧，C# 仅保留粒度选择纯函数。</summary>
public class DeadlockTrendTests
{
    [Fact]
    public void 桶粒度_跨度自适应()
    {
        Assert.Equal("minute", DeadlockService.BucketUnit(TimeSpan.FromHours(6)));
        Assert.Equal("minute", DeadlockService.BucketUnit(TimeSpan.FromMinutes(5)));
        Assert.Equal("hour", DeadlockService.BucketUnit(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1))));
        Assert.Equal("hour", DeadlockService.BucketUnit(TimeSpan.FromHours(48)));
        Assert.Equal("day", DeadlockService.BucketUnit(TimeSpan.FromDays(7)));
    }

    [Fact]
    public void LIKE转义_通配符与转义符全部加前缀()
    {
        Assert.Equal(@"100\%", "100%".EscapeSqlLike());
        Assert.Equal(@"a\_b", "a_b".EscapeSqlLike());
        Assert.Equal(@"x\[y", "x[y".EscapeSqlLike());
        Assert.Equal(@"c\\d", @"c\d".EscapeSqlLike());
        // 普通字符（含中文对象名）不受影响
        Assert.Equal("dbo.订单表", "dbo.订单表".EscapeSqlLike());
    }
}
