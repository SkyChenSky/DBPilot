namespace DBPilot.Core.TopSql;

/// <summary>实时/历史展示行共用的占比计算契约（AttachPercents 泛型共用，字段名与类型两行一致）。</summary>
internal interface ITopSqlPercentRow
{
    long ExecutionCount { get; }
    double TotalElapsedMs { get; }
    double TotalCpuMs { get; }
    long TotalLogicalReads { get; }

    double ExecutionCountPercent { get; set; }
    double TotalElapsedPercent { get; set; }
    double TotalCpuPercent { get; set; }
    double LogicalReadsPercent { get; set; }
}

/// <summary>
/// Provider 原始行（dm_exec_query_stats 按 query_hash 分组聚合，时间为微秒累计）。
/// </summary>
public class TopSqlRawRow
{
    /// <summary>query_hash hex；NULL 时 sql_handle hex 兜底（SQL 端处理）。</summary>
    public string Fingerprint { get; set; } = "";
    public string? DbName { get; set; }
    /// <summary>statement 级文本（按 offset 截取），同指纹取 MAX。</summary>
    public string? SqlText { get; set; }
    /// <summary>完整批处理文本（st.text 全文），同指纹取 MAX。</summary>
    public string? FullSqlText { get; set; }
    public long ExecutionCount { get; set; }
    public long TotalElapsedUs { get; set; }
    public long TotalWorkerUs { get; set; }
    public long TotalLogicalReads { get; set; }
    public long TotalPhysicalReads { get; set; }
    public long TotalWrites { get; set; }

    /// <summary>单次最大耗时（µs）。</summary>
    public long MaxElapsedUs { get; set; }

    /// <summary>已转 UTC。</summary>
    public DateTime? LastExecutionTime { get; set; }
}

/// <summary>实时 Top SQL 展示行（μs 已转 ms，含平均值）。</summary>
public class TopSqlRealtimeItem : ITopSqlPercentRow
{
    public string Fingerprint { get; set; } = "";
    public string? DbName { get; set; }
    public string SqlText { get; set; } = "";
    /// <summary>完整批处理文本（点击详情与 Statement 对照展示）。</summary>
    public string FullSqlText { get; set; } = "";
    public long ExecutionCount { get; set; }
    public double TotalElapsedMs { get; set; }
    public double AvgElapsedMs { get; set; }
    public double TotalCpuMs { get; set; }
    public double AvgCpuMs { get; set; }
    public long TotalLogicalReads { get; set; }
    public long TotalPhysicalReads { get; set; }
    public DateTime? LastExecutionTimeUtc { get; set; }

    // 占比（对齐阿里云展示；分母 = 展示的 TopN 行自身合计，TopN 变化占比随之变化）
    public double ExecutionCountPercent { get; set; }
    public double TotalElapsedPercent { get; set; }
    public double TotalCpuPercent { get; set; }
    public double LogicalReadsPercent { get; set; }
}

/// <summary>实时 Top SQL 结果（实例启动以来累计快照，轮询 10s）。</summary>
public class TopSqlRealtimeResult
{
    /// <summary>avg | total（排序口径，默认 avg 对齐阿里云）。</summary>
    public string Metric { get; set; } = "avg";
    public int TopN { get; set; }
    public List<TopSqlRealtimeItem> Items { get; set; } = [];
    public DateTime SnapshotTime { get; set; }
    /// <summary>累计统计起点（= 实例启动时间）。</summary>
    public DateTime? InstanceStartTimeUtc { get; set; }
    /// <summary>实例运行 &lt; 30 天：累计值可能不代表近期负载。</summary>
    public bool DataIncomplete { get; set; }
}

/// <summary>
/// Top SQL 查询过滤（RDS 内部查询排除，分层规则详见 SqlServerProvider.GetTopSqlRealtimeAsync 注释）：
/// 文本 NULL 与 RDS 官方标记两条身份规则固定在 SQL 内；本类承载可配置部分。
/// </summary>
public class TopSqlFilter
{
    /// <summary>兜底开关（页面“排除系统库”按钮传入）：“全部库”时额外排除 master 上下文行。</summary>
    public bool ExcludeSystemDb { get; set; }

    /// <summary>
    /// 无标记 RDS 巡检脚本文本特征（LIKE 模式，DBPilot:TopSqlExcludePatterns）。
    /// 注意 LIKE 语法：字面方括号须写 [[]，字面下划线须写 [_]。
    /// </summary>
    public List<string> Patterns { get; set; } = [];

    /// <summary>指纹黑名单（平台库 dbpilot_top_sql_exclusion，页面“排除”按钮维护，跨实例全局生效）。</summary>
    public List<string> Fingerprints { get; set; } = [];
}

/// <summary>历史 Top SQL 展示行（分钟差值按指纹+库聚合，含平均值）。</summary>
public class TopSqlHistoryItem : ITopSqlPercentRow
{
    public string Fingerprint { get; set; } = "";
    public string? DbName { get; set; }

    /// <summary>语句文本（dbpilot_sql_template；采集晚于首条 delta 时可能为空）。</summary>
    public string SqlText { get; set; } = "";
    public long ExecutionCount { get; set; }
    public double TotalElapsedMs { get; set; }
    public double AvgElapsedMs { get; set; }
    public double TotalCpuMs { get; set; }
    public double AvgCpuMs { get; set; }
    public long TotalLogicalReads { get; set; }
    public long TotalPhysicalReads { get; set; }
    public long TotalWrites { get; set; }

    /// <summary>窗口内单分钟差值的最大耗时。</summary>
    public double MaxElapsedMs { get; set; }

    /// <summary>首次/最近出现（差值窗口边界，UTC）。</summary>
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    // 占比（分母 = 展示的 TopN 行自身合计，口径同实时页）
    public double ExecutionCountPercent { get; set; }
    public double TotalElapsedPercent { get; set; }
    public double TotalCpuPercent { get; set; }
    public double LogicalReadsPercent { get; set; }
}

/// <summary>历史 Top SQL 结果（总榜：保留期内全量差值聚合）。</summary>
public class TopSqlHistoryResult
{
    /// <summary>total | avg | count | cpu | reads（排序口径，默认 total）。</summary>
    public string Metric { get; set; } = "total";
    public int TopN { get; set; }
    public List<TopSqlHistoryItem> Items { get; set; } = [];
}

/// <summary>历史聚合 SQL 投影行（SQL 别名 PascalCase 对齐属性，Chloe 精确映射）。</summary>
internal class TopSqlHistoryRawRow
{
    public string Fingerprint { get; set; } = "";
    public string? DbName { get; set; }
    public string? SqlText { get; set; }
    public long ExecutionCount { get; set; }
    public long TotalElapsedMs { get; set; }
    public long TotalWorkerMs { get; set; }
    public long TotalLogicalReads { get; set; }
    public long TotalPhysicalReads { get; set; }
    public long TotalWrites { get; set; }
    public long MaxElapsedMs { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}
