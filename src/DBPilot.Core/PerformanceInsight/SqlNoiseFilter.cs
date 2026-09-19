using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>
/// Load By SQL 噪音过滤（口径对齐 Top SQL 页，规则见 SqlServerProvider.GetTopSqlRealtimeAsync 注释）：
/// ② '/* rds internal mark */' 前缀（RDS 代理官方标记）+ '/* dbpilot */' 前缀（平台自身采集）；
/// ③ 配置 DBPilot:TopSqlExcludePatterns（SQL LIKE 模式，C# 侧等价转换）；
/// ④ 指纹黑名单 dbpilot_top_sql_exclusion（query_hash 精确匹配）。
/// 文本不可得的指纹只能被 ④ 命中（①③ 依赖文本，无文本不误杀）。
/// </summary>
public static class SqlNoiseFilter
{
    private const string RdsMark = "/* rds internal mark */";
    private const string DbpilotMark = "/* dbpilot */";

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public static bool IsExcluded(string fingerprint, string? text, HashSet<string> excludedFingerprints, List<string> likePatterns)
    {
        if (excludedFingerprints.Contains(fingerprint)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.TrimStart();
        if (t.StartsWith(RdsMark, StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(DbpilotMark, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var p in likePatterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (RegexCache.GetOrAdd(p.Trim(), static x => new Regex(LikeToRegex(x),
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)).IsMatch(t))
                return true;
        }
        return false;
    }

    /// <summary>SQL LIKE → 正则（% → .*，_ → .，[...] 字符类，未闭合 [ 视为字面量；对齐 SQL Server CI 排序规则忽略大小写）。</summary>
    internal static string LikeToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '%':
                    sb.Append(".*");
                    break;
                case '_':
                    sb.Append('.');
                    break;
                case '[' when i + 1 < pattern.Length:
                {
                    var j = i + 1;
                    var neg = pattern[j] == '^';
                    if (neg) j++;
                    var cls = new StringBuilder();
                    if (j < pattern.Length && pattern[j] == ']') cls.Append(']');   // ']' 紧随 '['/'[^' 为字面量
                    while (j < pattern.Length && pattern[j] != ']') cls.Append(pattern[j++]);
                    if (j >= pattern.Length) { sb.Append(Regex.Escape("[")); break; }   // 未闭合 → 字面量 [
                    var body = cls.ToString().Replace(@"\", @"\\").Replace("^", @"\^").Replace("-", @"\-");
                    sb.Append(neg ? "[^" + body + "]" : "[" + body + "]");
                    i = j;
                    break;
                }
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb + "$";
    }
}
