namespace DBPilot.Core.Providers;

using DBPilot.Core.Blocking;
using DBPilot.Core.Deadlocks;
using DBPilot.Core.Indexes;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.SlowSql;
using DBPilot.Core.TopSql;

/// <summary>
/// 被监控实例访问 Provider 抽象。
/// 多引擎并存：实现类以 <see cref="DbpilotEngineAttribute"/> 标识引擎
/// （与 dbpilot_instance.engine 列对应，取值见 <see cref="DbpilotEngines"/>），
/// 容器中注册的唯一 IDatabaseProvider 是 <see cref="RoutingDatabaseProvider"/>
/// （按实例配置 engine 路由到目标引擎 Provider）。
/// </summary>
public interface IDatabaseProvider
{

    /// <summary>连通性 + 权限自检：返回缺失权限与修复脚本。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(InstanceConfig cfg, CancellationToken ct = default);

    /// <summary>版本 / Edition / vCores / 启动时间探测（接入时一次）。</summary>
    Task<InstanceMeta> ProbeAsync(InstanceConfig cfg, CancellationToken ct = default);

    /// <summary>库列表（索引诊断等页面下拉用）。</summary>
    Task<List<string>> GetDatabasesAsync(InstanceConfig cfg, CancellationToken ct = default);

    /// <summary>缺失索引建议（逐库执行：连接串 Initial Catalog 指定目标库）。</summary>
    Task<List<MissingIndexItem>> GetMissingIndexesAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default);

    /// <summary>索引使用率（含键列/页数补充，逐库执行）。</summary>
    Task<List<IndexUsageItem>> GetIndexUsageAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default);

    /// <summary>碎片扫描候选表清单（用户表且堆/聚集总页数 ≥ minPages，按页数降序）。</summary>
    Task<List<FragTableInfo>> GetFragmentTablesAsync(InstanceConfig cfg, string dbName, int minPages, CancellationToken ct = default);

    /// <summary>单表索引碎片（dm_db_index_physical_stats LIMITED 模式，逐表分批调用）。</summary>
    Task<List<IndexFragmentItem>> GetIndexFragmentationAsync(InstanceConfig cfg, string dbName, int objectId, int minPages, CancellationToken ct = default);

    /// <summary>实时 Top SQL（dm_exec_query_stats 按 query_hash 分组聚合，μs 累计；db 为空 = 全部库；
    /// filter 承载可配置排除项 —— 巡检脚本特征模式（配置）+ 指纹黑名单（平台库）+ 系统库兜底开关）。</summary>
    Task<List<TopSqlRawRow>> GetTopSqlRealtimeAsync(InstanceConfig cfg, string db, TopSqlFilter? filter = null, CancellationToken ct = default);

    /// <summary>活动请求采样（dm_exec_requests + 会话信息 + statement 文本，μs→ms、本地时间→UTC 在 SQL 端；阻塞分析与性能洞察共用）。</summary>
    Task<List<ActiveRequestRow>> GetActiveRequestsAsync(InstanceConfig cfg, CancellationToken ct = default);

    /// <summary>头阻塞者补查（睡着拿锁特征；headSessionIds 为去重后的 blocking_session_id 集合）。</summary>
    Task<List<HeadBlockerRow>> GetHeadBlockersAsync(InstanceConfig cfg, List<int> headSessionIds, CancellationToken ct = default);

    /// <summary>阻塞原因（锁资源）：dm_tran_locks 查涉及会话的对象级锁（等待 + 持有），实体 id 已反查表名。</summary>
    Task<List<SessionLockRow>> GetSessionLocksAsync(InstanceConfig cfg, List<int> sessionIds, CancellationToken ct = default);

    /// <summary>死锁事件增量读取（版本双路径）：2012+ 读 system_health event_file（文件通配 + offset 游标）；
    /// 2008 幂等创建自建 DBPilot_Deadlock 会话（文件目标，XeFilePath 目录）增量读取，创建失败降级 system_health ring_buffer
    /// （timestamp 水位）。返回原始 event XML + 推进后游标 + 实例本地时区 offset。</summary>
    Task<DeadlockReadResult> ReadDeadlockEventsAsync(InstanceConfig cfg, DeadlockCursor? cursor, CancellationToken ct = default);

    /// <summary>慢SQL XE 会话保障：幂等创建 + 启动 DBPilot_SlowSql（版本分支：目标名 + duration 单位），
    /// rpc_completed(collect_statement=1) + sql_batch_completed，duration ≥ 实例阈值；创建/启动失败抛异常交上层标注退避。</summary>
    Task EnsureSlowSqlCaptureAsync(InstanceConfig cfg, CancellationToken ct = default);

    /// <summary>慢SQL事件增量读取（fn_xe file/offset 游标）：返回原始 event XML + 推进后游标。</summary>
    Task<SlowSqlReadResult> PollSlowSqlAsync(InstanceConfig cfg, SlowSqlCursor? cursor, CancellationToken ct = default);

    /// <summary>计划快照采集：dm_exec_query_stats 按 (指纹, plan_hash, plan_handle) 聚合的累计统计，
    /// 噪音排除与 GetTopSqlRealtimeAsync 同口径（filter 承载配置模式 + 指纹黑名单）。</summary>
    Task<List<QueryPlanRawRow>> GetQueryPlanStatsAsync(InstanceConfig cfg, TopSqlFilter? filter = null, CancellationToken ct = default);

    /// <summary>按 plan_handle + 语句偏移批量抓计划（仅缓存存活期内可查）：dm_exec_query_plan XML 优先，
    /// NULL 时兜 dm_exec_text_query_plan 语句级文本。返回键 = handle 小写 hex|start|end（QueryPlanHandleRef.Key）；
    /// 已被驱逐/两通道皆空的键由调用方自行判缺。</summary>
    Task<Dictionary<string, string>> GetQueryPlanXmlsAsync(InstanceConfig cfg, List<QueryPlanHandleRef> handles, CancellationToken ct = default);

    /// <summary>实例性能指标快照（趋势页，60s 采集）：CPU（ring buffer 最新 SystemHealth 采样）+
    /// 内存（dm_os_sys_memory / dm_os_process_memory，2008 列名版本分支）+ 全实例 IO 累计（dm_io_virtual_file_stats SUM）
    /// + 性能计数器原始行（value/base 配对在平台侧）；快照附带 ProductVersion 供降级诊断。</summary>
    Task<InstanceMetricsSnapshot> GetInstanceMetricsAsync(InstanceConfig cfg, CancellationToken ct = default);

    /// <summary>磁盘卷用量（dm_os_volume_stats，每卷一行）：2008（&lt; 10.5）无此 DMV，T-SQL 版本守卫包裹返回空集自然降级。</summary>
    Task<List<InstanceDiskRawRow>> GetInstanceDiskUsageAsync(InstanceConfig cfg, CancellationToken ct = default);
}
