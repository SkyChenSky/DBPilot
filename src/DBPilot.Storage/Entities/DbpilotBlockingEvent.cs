using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>阻塞事件留痕（dbpilot_blocking_event）。</summary>
[Table("dbpilot_blocking_event")]
public class DbpilotBlockingEvent
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("head_session_id")] public int HeadSessionId { get; set; }
    [Column("start_time")] public DateTime StartTime { get; set; }
    [Column("end_time")] public DateTime? EndTime { get; set; }
    [Column("blocked_count")] public int BlockedCount { get; set; }
    [Column("max_wait_seconds")] public int MaxWaitSeconds { get; set; }
    [Column("head_info")] public string? HeadInfo { get; set; }

    /// <summary>头阻塞者所在库（留痕时定格；历史筛选用）</summary>
    [Column("head_db_name")] public string? HeadDbName { get; set; }

    /// <summary>最近一次链路快照 JSON</summary>
    [Column("chain_tree")] public string ChainTree { get; set; } = string.Empty;

    [Column("resolved")] public bool Resolved { get; set; }
    [Column("create_time")] public DateTime CreateTime { get; set; }
    [Column("update_time")] public DateTime? UpdateTime { get; set; }
}
