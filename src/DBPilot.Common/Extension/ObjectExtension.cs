namespace DBPilot.Common;

/// <summary>
/// 对象扩展方法（移植自 SF.Tookits.ObjectExtension）。
/// </summary>
public static class ObjectExtension
{
    /// <summary>对象是 null。</summary>
    public static bool IsNull(this object? obj)
        => obj is null;

    /// <summary>对象不为 null。</summary>
    public static bool IsNotNull(this object? obj)
        => obj is not null;

    /// <summary>安全转字符串：null 返回空串。</summary>
    public static string ToStr(this object? input)
        => input?.ToString() ?? string.Empty;
}
