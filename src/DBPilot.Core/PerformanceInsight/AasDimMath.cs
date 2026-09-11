namespace DBPilot.Core.PerformanceInsight;

/// <summary>
/// AAS 维度数学（纯函数）：输入"每点 → 维度值 → 贡献"序列，
/// AAS = 各点贡献的均值（实时 = tick 计数均摊；历史 = 分钟贡献再均摊）。
/// </summary>
public static class AasDimMath
{
    /// <summary>区间内各维度值的 AAS 贡献，降序取前 N。</summary>
    public static List<(string Key, decimal Aas)> TopN(IReadOnlyList<Dictionary<string, decimal>> points, int n)
    {
        if (points.Count == 0) return [];
        var sums = SumBy(points);
        return sums.Select(kv => (kv.Key, Math.Round(kv.Value / points.Count, 4)))
                   .OrderByDescending(x => x.Item2)
                   .ThenBy(x => x.Key, StringComparer.Ordinal)
                   .Take(n)
                   .ToList();
    }

    /// <summary>该维度全部值的 AAS 合计（占比分母）。</summary>
    public static decimal TotalAverage(IReadOnlyList<Dictionary<string, decimal>> points)
        => points.Count == 0 ? 0 : Math.Round(SumBy(points).Values.Sum() / points.Count, 4);

    private static Dictionary<string, decimal> SumBy(IReadOnlyList<Dictionary<string, decimal>> points)
    {
        var sums = new Dictionary<string, decimal>();
        foreach (var p in points)
            foreach (var (k, v) in p)
                sums[k] = sums.GetValueOrDefault(k) + v;
        return sums;
    }

    /// <summary>单个维度值的时序（无样本的点为 null，前端断线显示）。</summary>
    public static List<decimal?> Series(IReadOnlyList<Dictionary<string, decimal>> points, string key)
        => points.Select(p => (decimal?)(p.TryGetValue(key, out var v) ? v : null)).ToList();
}
