using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DBPilot.Common;

/// <summary>
/// 字符串扩展方法（移植自 SF.Tookits.StringExtension，裁剪拼音等重依赖项）。
/// </summary>
public static class StringExtension
{
    #region 空判断

    public static bool IsNullOrEmpty([NotNullWhen(false)] this string? inputStr)
        => string.IsNullOrEmpty(inputStr);

    public static bool IsNullOrWhiteSpace([NotNullWhen(false)] this string? inputStr)
        => string.IsNullOrWhiteSpace(inputStr);

    #endregion

    #region 常用正则表达式

    private static readonly Regex EmailRegex = new(@"\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MobileRegex = new("^1[0-9]{10}$", RegexOptions.Compiled);

    private static readonly Regex IpRegex = new(@"^(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])$", RegexOptions.Compiled);

    private static readonly Regex NumericRegex = new(@"^[-]?[0-9]+(\.[0-9]+)?$", RegexOptions.Compiled);

    /// <summary>是否为邮箱名。</summary>
    public static bool IsEmail(this string? s)
        => !string.IsNullOrEmpty(s) && EmailRegex.IsMatch(s);

    /// <summary>是否为手机号。</summary>
    public static bool IsMobile(this string? s)
        => !string.IsNullOrEmpty(s) && MobileRegex.IsMatch(s);

    /// <summary>是否为 IP 地址。</summary>
    public static bool IsIp(this string? s)
        => !string.IsNullOrEmpty(s) && IpRegex.IsMatch(s);

    /// <summary>是否是数值（包括整数和小数）。</summary>
    public static bool IsNumeric(this string? numericStr)
        => !string.IsNullOrEmpty(numericStr) && NumericRegex.IsMatch(numericStr);

    #endregion

    #region 字符串截取

    /// <summary>
    /// 安全截取字符串：不足长度时返回原串，空串返回空串。
    /// </summary>
    public static string Sub(this string? inputStr, int length)
    {
        if (string.IsNullOrEmpty(inputStr))
            return string.Empty;

        return inputStr.Length >= length ? inputStr[..length] : inputStr;
    }

    #endregion

    #region 格式化文本

    /// <summary>
    /// 手机号脱敏：长度 &gt; 7 时中间四位替换为 ****。
    /// </summary>
    public static string FmtMobile(this string? mobile)
    {
        if (!string.IsNullOrEmpty(mobile) && mobile.Length > 7)
        {
            var regex = new Regex(@"(?<=\d{3}).+(?=\d{4})", RegexOptions.IgnoreCase);
            mobile = regex.Replace(mobile, "****");
        }

        return mobile ?? string.Empty;
    }

    /// <summary>会话来源格式化："登录名@主机（程序名）"；登录/主机缺省 "-"，程序名空则省略括号段。</summary>
    public static string FmtWho(this string? loginName, string? hostName, string? programName)
        => $"{loginName ?? "-"}@{hostName ?? "-"}{(string.IsNullOrEmpty(programName) ? "" : $"（{programName}）")}";

    #endregion

    #region 空白归一化 / T-SQL 转义

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 空白归一化：换行/制表等任意空白折叠为单个空格并去除首尾空白
    ///（SQL 预览/指纹归一化统一口径）。
    /// </summary>
    public static string NormalizeWhitespace(this string? inputStr)
        => inputStr.IsNullOrEmpty() ? string.Empty : WhitespaceRegex.Replace(inputStr, " ").Trim();

    /// <summary>
    /// T-SQL LIKE 通配符转义（% _ [ \ 前加 \，配合 SQL 端 ESCAPE N'\'；字面方括号须写 \[）。
    /// </summary>
    public static string EscapeSqlLike(this string inputStr)
        => inputStr.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");

    #endregion
}
