namespace DBPilot.Core.PerformanceInsight;

/// <summary>一分钟聚合结果（AAS + 等待桶 + 维度，写 dbpilot_active_request_sample 前的中间形态）。</summary>
public class MinuteAggregate
{
    public DateTime MinuteUtc { get; set; }

    /// <summary>该分钟内样本数（tick 数）。</summary>
    public int SampleCount { get; set; }

    /// <summary>AAS = Σ样本活跃会话数 / 样本数。</summary>
    public decimal AvgActive { get; set; }

    public int MaxActive { get; set; }

    /// <summary>桶 → AAS 贡献（该桶累计样本数 / 样本数）。</summary>
    public Dictionary<string, decimal> Buckets { get; set; } = [];

    /// <summary>维度 → Top10（维度值, 贡献）。</summary>
    public Dictionary<string, List<(string Key, decimal Value)>> Dims { get; set; } = [];
}

/// <summary>分钟聚合纯函数（落库 Job 与测试共用）。</summary>
public static class MinuteAggregator
{
    public const int DimTopN = 10;

    /// <summary>聚合一个完整分钟的 tick；无 tick 返回 null（不写库）。</summary>
    public static MinuteAggregate? Aggregate(DateTime minuteUtc, List<SampleTick> ticks)
    {
        if (ticks.Count == 0) return null;

        var agg = new MinuteAggregate
        {
            MinuteUtc = minuteUtc,
            SampleCount = ticks.Count,
            AvgActive = Math.Round((decimal)ticks.Sum(t => t.ActiveCount) / ticks.Count, 2),
            MaxActive = ticks.Max(t => t.ActiveCount),
        };

        foreach (var bucket in WaitBuckets.All)
        {
            var count = ticks.Sum(t => t.Buckets.GetValueOrDefault(bucket));
            if (count > 0) agg.Buckets[bucket] = Math.Round((decimal)count / ticks.Count, 4);
        }

        foreach (var dim in SampleTickBuilder.AllDims)
        {
            var counts = new Dictionary<string, int>();
            foreach (var t in ticks)
                foreach (var (key, count) in t.Dims.GetValueOrDefault(dim) ?? [])
                    counts[key] = counts.GetValueOrDefault(key) + count;

            agg.Dims[dim] = counts.OrderByDescending(kv => kv.Value)
                                  .Take(DimTopN)
                                  .Select(kv => (kv.Key, Math.Round((decimal)kv.Value / ticks.Count, 4)))
                                  .ToList();
        }

        return agg;
    }
}
