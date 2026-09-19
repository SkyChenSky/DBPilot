using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>
/// 死锁事件（dbpilot_deadlock_event：事实行 + 原始 XML）。
/// 进程/资源分析维度拆到 dbpilot_deadlock_process / dbpilot_deadlock_resource；
/// 详情展示用 DeadlockReportParser 按存储的时区偏移重解析 XML（单一事实源，无双份 JSON 冗余）。
/// </summary>
[Table("dbpilot_deadlock_event")]
public class DbpilotDeadlockEvent
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("event_time")] public DateTime EventTime { get; set; }

    /// <summary>牺牲进程 SPID，逗号分隔</summary>
    [Column("victim_spids")] public string VictimSpids { get; set; } = string.Empty;

    /// <summary>死锁图哈希，去重/相似归并</summary>
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;

    /// <summary>原始 &lt;deadlock&gt; XML 全文（审计/详情重解析/AI 兜底）</summary>
    [Column("deadlock_graph")] public string DeadlockGraph { get; set; } = string.Empty;

    /// <summary>采集时实例本地时间 - UTC（分钟）：详情重解析 XML 内本地时间属性用</summary>
    [Column("utc_offset_minutes")] public int UtcOffsetMinutes { get; set; }

    [Column("create_time")] public DateTime CreateTime { get; set; }
}
