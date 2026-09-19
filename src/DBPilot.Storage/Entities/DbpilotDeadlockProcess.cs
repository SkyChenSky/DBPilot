using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>
/// 死锁进程维度（一事件 N 行，SPID 行级明细 + 分析统计）。
/// 列表摘要 / 展开明细 / AI 统计（哪个登录/主机最常卷入死锁）共用；
/// 执行栈等长尾字段不入维度，详情由事件表 XML 重解析补齐。
/// </summary>
[Table("dbpilot_deadlock_process")]
public class DbpilotDeadlockProcess
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("event_id")] public int EventId { get; set; }
    [Column("instance_id")] public int InstanceId { get; set; }

    /// <summary>冗余事件时刻（Housekeeping 滚动清理与主表同节奏，免 JOIN）</summary>
    [Column("event_time")] public DateTime EventTime { get; set; }

    [Column("spid")] public int Spid { get; set; }
    [Column("is_victim")] public bool IsVictim { get; set; }
    [Column("login_name")] public string? LoginName { get; set; }
    [Column("host_name")] public string? HostName { get; set; }
    [Column("client_app")] public string? ClientApp { get; set; }
    /// <summary>隔离级别原文（如 "read committed (2)"）</summary>
    [Column("isolation_level")] public string? IsolationLevel { get; set; }
    /// <summary>本进程申请的锁模式（X / S / U …）</summary>
    [Column("lock_mode")] public string? LockMode { get; set; }
    [Column("wait_resource")] public string? WaitResource { get; set; }
    [Column("status")] public string? Status { get; set; }
    /// <summary>事务名（user_transaction / implicit …）</summary>
    [Column("transaction_name")] public string? TransactionName { get; set; }
    /// <summary>事务日志已用量（KB）</summary>
    [Column("log_used")] public int LogUsed { get; set; }
    [Column("wait_time_ms")] public int WaitTimeMs { get; set; }
    [Column("trancount")] public int Trancount { get; set; }
    /// <summary>事务开始（UTC，采集时已按实例时区转换）</summary>
    [Column("last_tran_started")] public DateTime? LastTranStarted { get; set; }
    /// <summary>完整 SQL（inputbuf，全文）</summary>
    [Column("input_buf")] public string? InputBuf { get; set; }

    [Column("create_time")] public DateTime CreateTime { get; set; }
}
