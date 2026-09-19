using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>
/// 死锁资源维度（一事件 M 行，锁类型/对象分析）。
/// 趋势按锁类型分色（COUNT(DISTINCT event_id)）、AI 统计"哪个对象最常死锁"的查询载体；
/// owner/waiter 边关系不入维度，详情由事件表 XML 重解析补齐。
/// </summary>
[Table("dbpilot_deadlock_resource")]
public class DbpilotDeadlockResource
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("event_id")] public int EventId { get; set; }
    [Column("instance_id")] public int InstanceId { get; set; }

    /// <summary>冗余事件时刻（趋势 GROUP BY / Housekeeping 清理免 JOIN）</summary>
    [Column("event_time")] public DateTime EventTime { get; set; }

    /// <summary>资源类型（keylock / objectlock / pagelock / ridlock / exchangeEvent …）</summary>
    [Column("resource_type")] public string ResourceType { get; set; } = string.Empty;

    /// <summary>对象名（objectname 属性，keylock/objectlock 有值）</summary>
    [Column("object_name")] public string? ObjectName { get; set; }
    [Column("index_name")] public string? IndexName { get; set; }
    /// <summary>资源当前授权模式（X / IX …）</summary>
    [Column("lock_mode")] public string? LockMode { get; set; }

    [Column("create_time")] public DateTime CreateTime { get; set; }
}
