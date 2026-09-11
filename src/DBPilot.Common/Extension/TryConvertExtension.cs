using System.Globalization;

namespace DBPilot.Common;

/// <summary>
/// 安全类型转换扩展方法（移植自 SF.Tookits.TryConvertExtension，转换失败返回默认值）。
/// </summary>
public static class TryConvertExtension
{
    /// <summary>转 int，失败返回默认值。</summary>
    public static int TryInt(this object? input, int defaultNum = 0)
        => input is null ? defaultNum : (int.TryParse(input.ToString(), out var num) ? num : defaultNum);

    /// <summary>转 long，失败返回默认值。</summary>
    public static long TryLong(this object? input, long defaultNum = 0)
        => input is null ? defaultNum : (long.TryParse(input.ToString(), out var num) ? num : defaultNum);

    /// <summary>转 double，失败返回默认值。</summary>
    public static double TryDouble(this object? input, double defaultNum = 0)
        => input is null ? defaultNum : (double.TryParse(input.ToString(), out var num) ? num : defaultNum);

    /// <summary>转 decimal，失败返回默认值。</summary>
    public static decimal TryDecimal(this object? input, decimal defaultNum = 0)
        => input is null ? defaultNum : (decimal.TryParse(input.ToString(), out var num) ? num : defaultNum);

    /// <summary>
    /// 转 bool：支持 "true"/"false"，以及自定义真假值（默认 "1"/"0"）。
    /// </summary>
    public static bool TryBool(this object? input, bool defaultBool = false, string trueVal = "1", string falseVal = "0")
    {
        if (input is null)
            return defaultBool;

        var str = input.ToString();
        if (bool.TryParse(str, out var outBool))
            return outBool;

        if (trueVal == str)
            return true;
        if (falseVal == str)
            return false;

        return defaultBool;
    }

    /// <summary>转 DateTime，失败返回默认值。</summary>
    public static DateTime TryDateTime(this string? inputStr, DateTime defaultValue = default)
        => inputStr.IsNullOrEmpty() ? defaultValue
           : (DateTime.TryParse(inputStr, out var output) ? output : defaultValue);

    /// <summary>按指定格式转 DateTime，失败返回默认值。</summary>
    public static DateTime TryDateTime(this string? inputStr, string format, DateTime defaultValue = default)
        => inputStr.IsNullOrEmpty() ? defaultValue
           : (DateTime.TryParseExact(inputStr, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var output) ? output : defaultValue);

    /// <summary>转 Guid，失败返回默认值。</summary>
    public static Guid TryGuid(this object? input, Guid defaultValue = default)
        => input is null ? defaultValue : (Guid.TryParse(input.ToString(), out var guid) ? guid : defaultValue);
}
