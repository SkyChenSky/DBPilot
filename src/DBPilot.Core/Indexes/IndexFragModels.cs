namespace DBPilot.Core.Indexes;

/// <summary>碎片处置建议：&gt;30% REBUILD；10%~30% REORGANIZE；页数 &lt;1000 不建议处理（Action 仍给出，前端标注）。</summary>
public enum FragAction
{
    None = 0,
    Reorganize = 1,
    Rebuild = 2,
}

/// <summary>碎片扫描候选表（用户表且堆/聚集总页数 ≥ minPages）。</summary>
public class FragTableInfo
{
    public int ObjectId { get; set; }
    public string TableName { get; set; } = "";
    public long PageCount { get; set; }
}

/// <summary>单索引碎片行（sys.dm_db_index_physical_stats LIMITED 模式，逐表采集）。</summary>
public class IndexFragmentItem
{
    public string DbName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string IndexName { get; set; } = "";
    public string IndexType { get; set; } = "";
    public int PartitionNumber { get; set; }
    public double AvgFragmentationPercent { get; set; }
    public long PageCount { get; set; }
    /// <summary>LIMITED 模式下部分场景无值。</summary>
    public long? RecordCount { get; set; }
    public FragAction Action { get; set; }
    /// <summary>页数 &lt; 1000：不建议处理（收益低于维护成本）。</summary>
    public bool SkipSmall { get; set; }
    public string? Script { get; set; }
}
