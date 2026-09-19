namespace DBPilot.Common;

/// <summary>
/// 集合扩展方法（移植自 SF.Tookits.EnumerableExtensions）。
/// </summary>
public static class EnumerableExtensions
{
    /// <summary>集合为 null 或长度为 0。</summary>
    public static bool IsNullOrEmpty<T>(this IEnumerable<T>? source)
        => source is null || !source.Any();

    /// <summary>集合不为 null 且长度大于 0。</summary>
    public static bool IsNotNullOrEmpty<T>(this IEnumerable<T>? source)
        => source is not null && source.Any();
}
