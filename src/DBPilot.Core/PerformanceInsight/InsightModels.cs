namespace DBPilot.Core.PerformanceInsight;

/// <summary>单个 AAS 点（实时 10s 粒度 / 历史 1min 粒度共用）。</summary>
public class AasPoint
{
    public DateTime TimeUtc { get; set; }
    /// <summary>活跃会话数（实时 = tick 计数；历史 = 分钟均值）。</summary>
    public decimal Active { get; set; }
    /// <summary>桶 → 贡献（实时 = 计数；历史 = 均值贡献）。</summary>
    public Dictionary<string, decimal> Buckets { get; set; } = [];
    /// <summary>维度 → 维度值 → 贡献（七维，AAS 分类切换 / Load By SQL 用）。</summary>
    public Dictionary<string, Dictionary<string, decimal>> Dims { get; set; } = [];
}

/// <summary>实时 AAS（内存环形缓冲直出，近 1h）。</summary>
public class AasRealtimeResult
{
    public int? CpuCores { get; set; }
    /// <summary>桶 → 中文提示名（前端 tooltip；图例直接用桶 key）。</summary>
    public Dictionary<string, string> BucketNames { get; set; } = [];
    /// <summary>最新采样时刻（null = 启动后尚无样本）。</summary>
    public DateTime? LastTickUtc { get; set; }
    public List<AasPoint> Points { get; set; } = [];
}

/// <summary>历史 AAS（dbpilot_active_request_sample 分钟聚合，近 24h / 自定义 ≤7 天）。</summary>
public class AasHistoryResult
{
    public int? CpuCores { get; set; }
    public Dictionary<string, string> BucketNames { get; set; } = [];
    public List<AasHistoryPoint> Points { get; set; } = [];
}

public class AasHistoryPoint : AasPoint
{
    public int MaxActive { get; set; }
    public int SampleCount { get; set; }
}

/// <summary>Load By SQL 行：指纹 + 区间 AAS 贡献 + 占比 + 语句文本（文本缺失时前端显示指纹）。</summary>
public class TopSqlItem
{
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>区间平均活跃会话贡献（如 0.445 = 平均 0.445 个活跃会话在跑这条）。</summary>
    public decimal Aas { get; set; }
    /// <summary>占该维度总 AAS 的百分比（0~100，1 位小数）。</summary>
    public decimal Percent { get; set; }
    /// <summary>该 SQL 的 AAS 构成：等待桶 → 区间均值贡献（历史路径受分钟 Top10 截断为近似值）。</summary>
    public Dictionary<string, decimal> Buckets { get; set; } = [];
    public string? SqlText { get; set; }
}

/// <summary>单条 SQL 的 AAS 趋势（点击 Load By SQL 行查看）。</summary>
public class SqlTrendResult
{
    public string Fingerprint { get; set; } = string.Empty;
    public string? SqlText { get; set; }
    /// <summary>粒度：10s（实时缓冲）或 1min（分钟聚合表）。</summary>
    public string Granularity { get; set; } = "10s";
    public List<SqlTrendPoint> Points { get; set; } = [];
}

public class SqlTrendPoint
{
    public DateTime TimeUtc { get; set; }
    /// <summary>该点贡献（实时 = tick 内计数；历史 = 分钟内均值贡献）；null = 该点无样本。</summary>
    public decimal? Value { get; set; }
}
