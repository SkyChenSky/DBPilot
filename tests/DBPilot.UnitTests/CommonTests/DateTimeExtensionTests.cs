using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class DateTimeExtensionTests
{
    [Fact]
    public void FirstDayOfWeek_周一为一周开始()
    {
        // 2026-08-20 是周四 → 本周一 2026-08-17
        var d = new DateTime(2026, 8, 20, 10, 30, 0);
        Assert.Equal(new DateTime(2026, 8, 17), d.FirstDayOfWeek());
    }

    [Fact]
    public void FirstDayOfWeek_跨月()
    {
        // 2026-08-01 是周六 → 本周一为 7 月 27 日
        var d = new DateTime(2026, 8, 1);
        Assert.Equal(new DateTime(2026, 7, 27), d.FirstDayOfWeek());
    }

    [Fact]
    public void FirstDayOfMonth()
    {
        var d = new DateTime(2026, 8, 20, 10, 30, 0);
        Assert.Equal(new DateTime(2026, 8, 1), d.FirstDayOfMonth());
    }

    [Fact]
    public void LastDayOfMonth_最后一毫秒()
    {
        var d = new DateTime(2026, 8, 20);
        Assert.Equal(new DateTime(2026, 8, 31, 23, 59, 59, 999), d.LastDayOfMonth());
    }

    [Fact]
    public void LastDayOfYear_最后一毫秒()
    {
        var d = new DateTime(2026, 8, 20);
        Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 59, 999), d.LastDayOfYear());
    }
}
