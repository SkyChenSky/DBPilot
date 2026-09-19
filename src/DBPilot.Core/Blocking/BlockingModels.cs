namespace DBPilot.Core.Blocking;

/// <summary>
/// 被监控实例活动请求行（按需查询版：页面 5s 轮询直查 dm_exec_requests，
/// 不走定时采样 Job —— 采样底座（SessionSampleJob）留痕复用本查询）。
/// </summary>
public class ActiveRequestRow
{
    public int SessionId { get; set; }
    public string Status { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>请求开始时间（已转 UTC）。</summary>
    public DateTime? StartTimeUtc { get; set; }
    public string? WaitType { get; set; }
    /// <summary>当前等待已等待毫秒（DMV wait_time 原生 ms）。</summary>
    public long WaitTimeMs { get; set; }
    public string? WaitResource { get; set; }
    /// <summary>阻塞来源会话；-2 = 孤儿分布式事务、-3 = 延迟恢复（系统节点，不可 Kill）。</summary>
    public int BlockingSessionId { get; set; }
    public long TotalElapsedMs { get; set; }
    public long CpuMs { get; set; }
    public int OpenTranCount { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? ProgramName { get; set; }
    public string? DbName { get; set; }
    /// <summary>statement 级文本（按 offset 截取；系统进程等取全文）。</summary>
    public string? SqlText { get; set; }
    /// <summary>整个批处理全文（sql_handle 对应的 st.text，含批内已执行过的语句，如拿锁的 UPDATE）。</summary>
    public string? BatchSqlText { get; set; }
    /// <summary>语句级 query_hash（hex，SQL 端 CONVERT；未命中为 null → 采样侧文本指纹兜底）。</summary>
    public string? QueryHash { get; set; }
}

/// <summary>
/// 头阻塞者补查行：阻塞链头"睡着拿锁"——事务开着、锁拿着，
/// 但 dm_exec_requests 无活动请求（AWOL 会话），主查询看不到，需按 blocking_session_id 定点补查。
/// </summary>
public class HeadBlockerRow
{
    public int SessionId { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? ProgramName { get; set; }
    /// <summary>会话当前数据库（SQL Server DB_NAME(database_id) / MySQL PROCESSLIST_DB）——留痕与实时页的"数据库"列。</summary>
    public string? DbName { get; set; }
    public int OpenTranCount { get; set; }
    /// <summary>当前最早活动事务开始时间（已转 UTC）——锁持有时长的下界。</summary>
    public DateTime? TransactionBeginUtc { get; set; }
    /// <summary>最后执行的语句文本（most_recent_sql_handle，非当前正在执行——睡着即无当前请求）。</summary>
    public string? LastSqlText { get; set; }
}

/// <summary>
/// 会话锁资源行（阻塞原因）：dm_tran_locks 对象级锁（WAIT = 正在等的锁、GRANT = 已持有的锁），
/// ObjectName 由 Provider 侧按 resource_associated_entity_id 反查（object_id / hobt_id / allocation_unit_id）。
/// </summary>
public class SessionLockRow
{
    public int SessionId { get; set; }
    public string ResourceType { get; set; } = "";
    public string? DbName { get; set; }
    /// <summary>资源实体 id（object_id / partition_id / allocation_unit_id；DATABASE 等无实体为 0）。</summary>
    public long EntityId { get; set; }
    /// <summary>锁模式（S / X / IX / IS / U / Sch-M …）。</summary>
    public string LockMode { get; set; } = "";
    /// <summary>GRANT = 已持有 / WAIT = 正在等待 / CONVERT = 转换中。</summary>
    public string LockStatus { get; set; } = "";
    /// <summary>反查出的 schema.表名（未命中为 null）。</summary>
    public string? ObjectName { get; set; }
}

/// <summary>阻塞树节点上的锁展示（阻塞原因）。</summary>
public class BlockingLockInfo
{
    public string ResourceType { get; set; } = "";
    public string? DbName { get; set; }
    public string? ObjectName { get; set; }
    public string LockMode { get; set; } = "";
    public string LockStatus { get; set; } = "";
}

/// <summary>阻塞树节点（平台侧组装）。</summary>
public class BlockingNode
{
    public int SessionId { get; set; }
    /// <summary>blocking_session_id = -2 / -3：孤儿分布式事务 / 延迟恢复，系统节点。</summary>
    public bool IsSystem { get; set; }
    /// <summary>头阻塞者"睡着拿锁"（无活动请求但持有事务锁）。</summary>
    public bool IsSleepingHead { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? ProgramName { get; set; }
    public string? DbName { get; set; }
    public string? Status { get; set; }
    public string? Command { get; set; }
    public string? WaitType { get; set; }
    public long WaitTimeMs { get; set; }
    public string? WaitResource { get; set; }
    /// <summary>请求已执行时长（睡着头为 null）。</summary>
    public long? TotalElapsedMs { get; set; }
    public int OpenTranCount { get; set; }
    public string? SqlText { get; set; }
    /// <summary>完整批处理文本（活动请求 = sql_handle 批全文；睡着头 = input_buffer）。</summary>
    public string? BatchSqlText { get; set; }
    /// <summary>头阻塞者的树内深度（root = 0，直接被阻塞 = 1 …）。</summary>
    public int Depth { get; set; }
    public List<BlockingNode> Children { get; set; } = [];
    /// <summary>阻塞原因：该会话的锁资源（等待中的在前，已持有的在后；每会话最多 10 条）。</summary>
    public List<BlockingLockInfo> Locks { get; set; } = [];
}

/// <summary>历史阻塞事件列表行（dbpilot_blocking_event 分页投影，时间均为 UTC）。</summary>
public class BlockingEventListItem
{
    public int Id { get; set; }
    public int HeadSessionId { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime? EndTimeUtc { get; set; }
    /// <summary>被阻塞会话数（最近一次采样快照）。</summary>
    public int BlockedCount { get; set; }
    /// <summary>链上最长等待秒数（最近一次采样快照）。</summary>
    public int MaxWaitSeconds { get; set; }
    /// <summary>头阻塞者摘要（login@host（program），留痕时定格）。</summary>
    public string? HeadInfo { get; set; }
    /// <summary>头阻塞者所在库（留痕时定格；历史筛选用，老留痕为 null）。</summary>
    public string? HeadDbName { get; set; }
    /// <summary>头阻塞者 SQL 预览（chain_tree 根节点语句级文本，缺省兜底整批；最长 200）。</summary>
    public string? HeadSql { get; set; }
    public bool Resolved { get; set; }
}

/// <summary>阻塞数量统计（历史 tab 顶部卡片：近一天 / 近一周 / 近两周，按开始时间口径）。</summary>
public class BlockingStats
{
    public int DayCount { get; set; }
    public int WeekCount { get; set; }
    public int TwoWeekCount { get; set; }
}

/// <summary>阻塞趋势时间桶（历史 tab 趋势图：次数 + 累计最长等待秒）。</summary>
public class BlockingTrendItem
{
    public DateTime BucketStartUtc { get; set; }
    public int Count { get; set; }
    /// <summary>桶内各事件"最长等待秒"累计（阻塞严重度趋势）。</summary>
    public long TotalWaitSeconds { get; set; }
}

/// <summary>阻塞趋势（按时间桶聚合，桶粒度按查询跨度自适应）。</summary>
public class BlockingTrend
{
    /// <summary>桶粒度分钟数（前端 X 轴标签格式化参考）。</summary>
    public int StepMinutes { get; set; }
    public List<BlockingTrendItem> Items { get; set; } = [];
}

/// <summary>历史阻塞事件详情：事件元信息 + 最近一次链路快照（阻塞树）。</summary>
public class BlockingEventDetail : BlockingEventListItem
{
    public int InstanceId { get; set; }
    /// <summary>链路快照（chain_tree 反序列化；脏数据容错为 null）。进行中事件的快照随采样持续刷新。</summary>
    public BlockingNode? Tree { get; set; }
}

/// <summary>当前阻塞总览（链数 / 最长等待 / 涉及会话数 + 阻塞树森林）。</summary>
public class BlockingOverview
{
    /// <summary>阻塞链（根阻塞者）数量。</summary>
    public int ChainCount { get; set; }
    /// <summary>链上最长等待秒数。</summary>
    public long MaxWaitSeconds { get; set; }
    /// <summary>涉及会话总数（含头阻塞者与系统节点）。</summary>
    public int InvolvedSessions { get; set; }
    public DateTime SnapshotTimeUtc { get; set; }
    /// <summary>阻塞树（每棵 = 一个根阻塞者；树间互不相交）。</summary>
    public List<BlockingNode> Trees { get; set; } = [];
}
