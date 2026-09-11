namespace DBPilot.Common;

/// <summary>
/// 日期扩展方法（移植自 SF.Tookits.DateTimeExtension）。
/// </summary>
public static class DateTimeExtension
{
    /// <summary>指定日期所在周的第一天（周一，0 点）。</summary>
    public static DateTime FirstDayOfWeek(this DateTime datetime)
    {
        var numOfWeek = Convert.ToInt32(datetime.DayOfWeek);
        numOfWeek = numOfWeek == 0 ? 7 - 1 : numOfWeek - 1;

        return datetime.AddDays(-1 * numOfWeek).Date;
    }

    /// <summary>指定日期所在月的第一天（0 点）。</summary>
    public static DateTime FirstDayOfMonth(this DateTime datetime)
        => datetime.AddDays(1 - datetime.Day).Date;

    /// <summary>指定日期所在月的最后一天（最后一毫秒）。</summary>
    public static DateTime LastDayOfMonth(this DateTime datetime)
        => datetime.FirstDayOfMonth().AddMonths(1).AddMilliseconds(-1);

    /// <summary>指定日期所在年的最后一天（最后一毫秒）。</summary>
    public static DateTime LastDayOfYear(this DateTime datetime)
        => new DateTime(datetime.Year, 1, 1).AddYears(1).AddMilliseconds(-1);

    /// <summary>
    /// 截断到毫秒（保留 Kind）：XE 时间戳亚毫秒精度 vs DATETIME2(3) 网格，未截断值与库中回读值精确比较会失配。
    /// </summary>
    public static DateTime TruncateToMillisecond(this DateTime datetime)
        => new(datetime.Ticks - datetime.Ticks % TimeSpan.TicksPerMillisecond, datetime.Kind);
}
