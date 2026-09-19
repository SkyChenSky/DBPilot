namespace DBPilot.Core.Deadlocks;

/// <summary>
/// 死锁进程（辅助信息载体）：XE xml_deadlock_report 的 process 节点。
/// </summary>
public class DeadlockProcess
{
    /// <summary>XE 进程 id（processXXXXXXXX，资源 owner/waiter 引用它）。</summary>
    public string Id { get; set; } = "";

    public int Spid { get; set; }
    public bool IsVictim { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? ClientApp { get; set; }
    /// <summary>隔离级别原文（如 "read committed (2)"）。</summary>
    public string? IsolationLevel { get; set; }
    /// <summary>本进程申请的锁模式（X / S / U …）。</summary>
    public string? LockMode { get; set; }
    public string? WaitResource { get; set; }
    public string? Status { get; set; }
    /// <summary>事务名（user_transaction / implicit …）。</summary>
    public string? TransactionName { get; set; }
    /// <summary>当前库 id（XE 原生是数字，库名需另查）。</summary>
    public int CurrentDatabaseId { get; set; }
    public int Trancount { get; set; }
    /// <summary>事务日志已用量（KB）。</summary>
    public int LogUsed { get; set; }
    public int WaitTimeMs { get; set; }
    public int TaskPriority { get; set; }

    /// <summary>事务开始 / 最后批次起止（XML 中为实例本地时间，解析时按 offset 转 UTC）。</summary>
    public DateTime? LastTranStartedUtc { get; set; }
    public DateTime? LastBatchStartedUtc { get; set; }
    public DateTime? LastBatchCompletedUtc { get; set; }

    /// <summary>完整 SQL（inputbuf）。</summary>
    public string? InputBuf { get; set; }

    /// <summary>执行栈（前 5 帧）。</summary>
    public List<string> ExecutionStack { get; set; } = [];
}

/// <summary>死锁资源节点 + 持有者/等待者（关系图的资源侧）。</summary>
public class DeadlockResource
{
    /// <summary>XE 锁 id（lockXXXXXXXX）。</summary>
    public string Id { get; set; } = "";

    /// <summary>资源类型（keylock / objectlock / pagelock / ridlock / exchangeEvent …）。</summary>
    public string ResourceType { get; set; } = "";

    /// <summary>资源描述：objectlock = schema.表名；keylock/ridlock = 表名(索引)；其余 = 原始属性摘要。</summary>
    public string Display { get; set; } = "";

    public string? ObjectName { get; set; }
    public string? IndexName { get; set; }
    public string? DatabaseId { get; set; }
    /// <summary>资源当前授权模式（X / IX …）。</summary>
    public string? Mode { get; set; }

    public List<DeadlockProcessRef> Owners { get; set; } = [];
    public List<DeadlockProcessRef> Waiters { get; set; } = [];
}

/// <summary>资源侧的进程引用（id + 模式）。</summary>
public class DeadlockProcessRef
{
    public string ProcessId { get; set; } = "";
    /// <summary>持有 / 申请的锁模式。</summary>
    public string Mode { get; set; } = "";
}

/// <summary>一次死锁事件的完整解析结果。</summary>
public class DeadlockEventModel
{
    /// <summary>死锁发生时刻（XE event timestamp，UTC）。</summary>
    public DateTime EventTimeUtc { get; set; }

    public List<string> VictimProcessIds { get; set; } = [];
    public List<DeadlockProcess> Processes { get; set; } = [];
    public List<DeadlockResource> Resources { get; set; } = [];

    /// <summary>图指纹（SHA256 前 16 hex）：相似死锁归并去重。</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>原始 &lt;deadlock&gt; XML 全文（落库 deadlock_graph 列，详情按需重解析）。</summary>
    public string GraphXml { get; set; } = "";
}

/// <summary>XE 死锁事件原始行（fn_xe_file_target_read_file 返回；解析在平台侧）。</summary>
public class DeadlockEventRow
{
    /// <summary>event XML 全文（含 timestamp 属性与 xml_report）。</summary>
    public string EventData { get; set; } = "";
}

/// <summary>XE 增量读取游标：event_file 用 (文件名, offset)；ring_buffer 降级用 timestamp 水位。</summary>
public class DeadlockCursor
{
    public string? FileName { get; set; }
    public long Offset { get; set; }
    public DateTime? TimestampUtc { get; set; }
}

/// <summary>一次死锁增量读取的结果（Provider 出参）。</summary>
public class DeadlockReadResult
{
    public List<DeadlockEventRow> Events { get; set; } = [];
    /// <summary>推进后的游标（无论是否有新事件都前进到已读位置）；null = 尚无游标（首扫前/保留旧空游标）。</summary>
    public DeadlockCursor? NewCursor { get; set; }
    /// <summary>实例本地时间 - UTC（分钟级；进程属性本地时间转 UTC 用）。</summary>
    public TimeSpan LocalUtcOffset { get; set; }
}

/// <summary>死锁列表行（留痕表分页投影 + processes/resources JSON 摘要提取）。</summary>
public class DeadlockListItem
{
    public int Id { get; set; }
    public DateTime EventTimeUtc { get; set; }
    /// <summary>牺牲进程 spid（逗号分隔原文）。</summary>
    public string VictimSpids { get; set; } = "";
    /// <summary>牺牲进程摘要：login@host（program）。</summary>
    public string? VictimSummary { get; set; }
    /// <summary>其余进程摘要（多进程死锁全部列出，" ｜ "连接）。</summary>
    public string? OtherSummary { get; set; }
    /// <summary>参与方进程数（含牺牲方；列表"参与方"列 + 展开行级明细的数据来源）。</summary>
    public int ProcessCount { get; set; }
    /// <summary>涉及对象（去重，" ｜ "连接；含 keylock 索引名）。</summary>
    public string Objects { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>死锁趋势点（锁资源类型分色堆叠；分色计数 = 事件 × 涉及类型各计一次，Total = 按事件计不重复）。</summary>
public class DeadlockTrendPoint
{
    public DateTime TimeUtc { get; set; }
    /// <summary>该桶死锁事件数（一次事件只计 1，与分色计数口径区分）。</summary>
    public int Total { get; set; }
    public int KeyLocks { get; set; }
    public int ObjectLocks { get; set; }
    public int PageLocks { get; set; }
    public int RidLocks { get; set; }
    /// <summary>exchangeEvent / metadatalock / threadlock 等归并。</summary>
    public int OtherLocks { get; set; }
}

/// <summary>趋势 Total 查询投影行（主表按事件计数）。</summary>
internal class TrendTotalRow
{
    public DateTime TimeUtc { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// 死锁趋势-only 降级形态（MySQL/PostgreSQL 无事件明细，读指标序列聚合）：
/// 每桶死锁速率均值，不伪造事件计数。
/// </summary>
public class DeadlockMetricsTrend
{
    /// <summary>桶宽秒（与指标趋势 API 同档自适应）。</summary>
    public int BucketSeconds { get; set; }
    public List<DateTime> Times { get; set; } = [];
    /// <summary>每桶 Number of Deadlocks/sec 均值（无样本桶为 null 断点）。</summary>
    public List<decimal?> DeadlocksPerSec { get; set; } = [];
}

/// <summary>列表分页 SQL 投影行（SQL 别名 PascalCase 对齐属性，Chloe 精确映射）。</summary>
internal class DeadlockPageRow
{
    public int Id { get; set; }
    public DateTime EventTime { get; set; }
    public string VictimSpids { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>列表过滤下拉选项（进程维度 distinct，时间窗与列表一致）。</summary>
public class DeadlockFilterOptions
{
    public List<string> LoginNames { get; set; } = [];
    public List<string> HostNames { get; set; } = [];
}

/// <summary>指纹归并 SQL 聚合投影行（LastEventId 用于回查最近一次事件补摘要）。</summary>
internal class DeadlockFingerprintRow
{
    public string Fingerprint { get; set; } = "";
    public int Count { get; set; }
    public DateTime FirstTimeUtc { get; set; }
    public DateTime LastTimeUtc { get; set; }
    public int LastEventId { get; set; }
}

/// <summary>相似死锁归并统计（同指纹 = 同构死锁反复发生；摘要取最近一次事件）。</summary>
public class DeadlockFingerprintStat
{
    public string Fingerprint { get; set; } = "";
    public int Count { get; set; }
    public DateTime FirstTimeUtc { get; set; }
    public DateTime LastTimeUtc { get; set; }
    /// <summary>最近一次事件的涉及对象摘要。</summary>
    public string Objects { get; set; } = "";
    /// <summary>最近一次事件的牺牲进程摘要。</summary>
    public string? VictimSummary { get; set; }
}

/// <summary>死锁详情（节点/边结构化数据 + 原始 XML）。</summary>
public class DeadlockDetail : DeadlockListItem
{
    public int InstanceId { get; set; }
    public List<DeadlockProcess> Processes { get; set; } = [];
    public List<DeadlockResource> Resources { get; set; } = [];
    /// <summary>原始 deadlock XML（兜底/导出用；关系图用 Processes/Resources 结构化数据）。</summary>
    public string GraphXml { get; set; } = "";
}
