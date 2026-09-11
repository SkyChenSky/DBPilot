namespace DBPilot.Core.Indexes;

/// <summary>
/// 缺失索引建议行（CreateIndexSql 由平台生成）。
/// </summary>
public class MissingIndexItem
{
    /// <summary>所属库（"全部库"聚合时用于区分）。</summary>
    public string DbName { get; set; } = string.Empty;

    /// <summary>mid.statement，形如 [dbo].[Orders]（DMV 原样输出，已带括号）。</summary>
    public string TableName { get; set; } = string.Empty;

    public string? EqualityColumns { get; set; }
    public string? InequalityColumns { get; set; }
    public string? IncludedColumns { get; set; }

    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public double AvgTotalUserCost { get; set; }
    public double AvgUserImpact { get; set; }
    public DateTime? LastUserSeek { get; set; }

    /// <summary>user_seeks × avg_total_user_cost × avg_user_impact × 0.01（微软算法，SQL 端计算）。</summary>
    public double Score { get; set; }

    /// <summary>表已用页数（堆/聚集分区聚合）—— 过滤"表 ≥ 100 页"用。</summary>
    public long? TablePages { get; set; }

    /// <summary>表行数 —— 过滤"表 ≥ 1000 条"用。</summary>
    public long? TableRows { get; set; }

    /// <summary>一键创建脚本（ONLINE=ON 仅 Enterprise）。</summary>
    public string CreateIndexSql { get; set; } = string.Empty;
}

/// <summary>缺失索引总览统计（对齐阿里云：总量 / 高收益 / 近期访问）。</summary>
public class MissingIndexOverview
{
    public int Total { get; set; }
    /// <summary>性能提升（avg_user_impact）&gt; 80%。</summary>
    public int HighImpact { get; set; }
    public int LastDayCount { get; set; }
    public int LastWeekCount { get; set; }
    public int LastMonthCount { get; set; }
    /// <summary>各维度占比（0~100，保留 0 位小数；总量 0 时为 0）。</summary>
    public double HighImpactPercent { get; set; }
    public double LastDayPercent { get; set; }
    public double LastWeekPercent { get; set; }
    public double LastMonthPercent { get; set; }
}

/// <summary>缺失索引快照行（每日追加的原始建议行，不合并不标注；IsNew = 近 7 天新增）。</summary>
public class MissingIndexSnapshotItem
{
    public string DbName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string? EqualityColumns { get; set; }
    public string? InequalityColumns { get; set; }
    public string? IncludedColumns { get; set; }
    public long UserSeeks { get; set; }
    public double AvgTotalUserCost { get; set; }
    public double AvgUserImpact { get; set; }
    public double Score { get; set; }
    public DateTime? LastUserSeek { get; set; }
    public long? TablePages { get; set; }
    public long? TableRows { get; set; }
    public string CreateIndexSql { get; set; } = string.Empty;

    /// <summary>近 7 天新增：组合（库+表+三组列）在 7 天前的历史快照中不存在。</summary>
    public bool IsNew { get; set; }
}

/// <summary>缺失索引最新快照结果（上次采集时间 + 近 7 天新增标记）。</summary>
public class MissingIndexSnapshotResult
{
    public List<MissingIndexSnapshotItem> Items { get; set; } = [];
    public MissingIndexOverview Overview { get; set; } = new();

    /// <summary>上次采集时间（每日 03:10 Job 统一时间戳）；null = 尚无快照。</summary>
    public DateTime? SnapshotTimeUtc { get; set; }
}

/// <summary>缺失索引变化趋势点（每个快照批次的建议条数，供前端柱状图）。</summary>
public class MissingIndexTrendPoint
{
    /// <summary>批次采集时间（UTC，同实例整批统一）。</summary>
    public DateTime SnapshotTimeUtc { get; set; }

    /// <summary>该批次落库的原始建议行数。</summary>
    public int Count { get; set; }
}

/// <summary>
/// 索引使用率行（使用率查询 + 键列/页数补充；IsUnused 由平台判定）。
/// </summary>
public class IndexUsageItem
{
    /// <summary>所属库（"全部库"聚合时用于区分）。</summary>
    public string DbName { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
    public string TypeDesc { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
    public bool IsUnique { get; set; }
    public string? FilterDefinition { get; set; }

    /// <summary>键列（不含 INCLUDE），逗号分隔 —— 冗余/前缀重叠分析用。</summary>
    public string? KeyColumns { get; set; }

    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public long UserLookups { get; set; }
    public long UserUpdates { get; set; }
    public DateTime? LastUserSeek { get; set; }
    public DateTime? LastUserScan { get; set; }
    public DateTime? LastUserUpdate { get; set; }

    /// <summary>已用页数（分区聚合）；估算索引大小。</summary>
    public long? UsedPageCount { get; set; }

    /// <summary>未使用判定：读=0 且写放大明显（脚本生成 ALTER INDEX ... DISABLE，保守方案）。</summary>
    public bool IsUnused { get; set; }
}

/// <summary>
/// 索引使用率最新快照行（使用率 + 未使用判定 + 碎片率合并口径：IsUnused 采集时固化、碎片率并入每日快照，
/// Action/FragScript 查询时按快照值计算）。
/// </summary>
public class IndexUsageSnapshotItem
{
    public string DbName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
    public string TypeDesc { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
    public bool IsUnique { get; set; }

    /// <summary>键列裸列名 "A,B"（不含 INCLUDE、不含 filter_definition）。</summary>
    public string? KeyColumns { get; set; }

    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public long UserLookups { get; set; }
    public long UserUpdates { get; set; }
    public DateTime? LastUserSeek { get; set; }
    public DateTime? LastUserScan { get; set; }
    public DateTime? LastUserUpdate { get; set; }

    /// <summary>已用页数（分区聚合）；前端按 ×8/1024 估算大小 MB。</summary>
    public long? UsedPageCount { get; set; }

    /// <summary>采集时固化的未使用判定（读=0 且写放大明显，非主键/唯一）。</summary>
    public bool IsUnused { get; set; }

    /// <summary>平均碎片率（LIMITED 模式，多分区归一 = MAX 最差分区；&lt;256 页无值为 null）。</summary>
    public double? AvgFragmentationPercent { get; set; }

    /// <summary>碎片处置建议（按碎片率；无碎片数据 = None）。</summary>
    public FragAction Action { get; set; }

    /// <summary>页数 &lt; 1000：不建议处理（收益低于维护成本）。</summary>
    public bool SkipSmall { get; set; }

    /// <summary>碎片处置脚本（Action=None 为空串；快照口径不带 PARTITION，整索引处理）。</summary>
    public string FragScript { get; set; } = string.Empty;

    /// <summary>未使用时的禁用脚本（否则 null）。</summary>
    public string? DisableScript { get; set; }
}

/// <summary>索引使用率最新快照结果（含运行时长警示：DMV 计数随实例重启清零）。</summary>
public class IndexUsageSnapshotResult
{
    public List<IndexUsageSnapshotItem> Items { get; set; } = [];
    public IndexUsageOverview Overview { get; set; } = new();
    public DateTime? InstanceStartTimeUtc { get; set; }
    public bool DataIncomplete { get; set; }

    /// <summary>上次采集时间（每日 Job 统一时间戳）；null = 尚无快照。</summary>
    public DateTime? SnapshotTimeUtc { get; set; }
}

/// <summary>索引使用率总览统计（对齐阿里云：总量/空间 / 碎片超标 / 低效访问）。</summary>
public class IndexUsageOverview
{
    public int Total { get; set; }

    /// <summary>总页数（used_page_count 求和）。</summary>
    public long TotalPages { get; set; }

    /// <summary>总空间 MB（页数 × 8KB / 1024，计算列）。</summary>
    public double TotalSpaceMb => TotalPages * 8.0 / 1024;

    /// <summary>碎片率 &gt; 30% 条数。</summary>
    public int FragOver30Count { get; set; }

    /// <summary>读次数（seek+scan+lookup）&lt; 100 条数。</summary>
    public int LowReadCount { get; set; }

    /// <summary>读占比 &lt; 10% 条数（分母 reads+updates = 0 不计入）。</summary>
    public int LowReadRatioCount { get; set; }
}

/// <summary>索引空间变化趋势点（每快照批次 used_page_count 求和，时间升序）。</summary>
public class IndexUsageTrendPoint
{
    /// <summary>批次采集时间（UTC，同实例整批统一）。</summary>
    public DateTime SnapshotTimeUtc { get; set; }

    /// <summary>该批次全部索引已用页数求和（空批次点为 0）。</summary>
    public long TotalPages { get; set; }
}
