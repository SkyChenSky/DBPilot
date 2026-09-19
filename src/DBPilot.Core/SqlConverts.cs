namespace DBPilot.Core;

/// <summary>采集/展示服务共用换算：Provider 侧 µs 累计值 → ms、落库未带 Kind 时间 → UTC。</summary>
internal static class SqlConverts
{
    /// <summary>µs → ms 整数毫秒（AwayFromZero 四舍五入；保留小数的展示版由各服务自持）。</summary>
    public static long UsToMs(long us) => (long)Math.Round(us / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>落库时间标 UTC Kind（null 安全）。</summary>
    public static DateTime? SpecifyUtc(DateTime? time)
        => time is null ? null : DateTime.SpecifyKind(time.Value, DateTimeKind.Utc);
}
