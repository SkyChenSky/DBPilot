using DBPilot.Core.Blocking;
using DBPilot.Core.Deadlocks;
using DBPilot.Core.Indexes;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.SlowSql;
using DBPilot.Core.TopSql;

namespace DBPilot.Core.Providers;

/// <summary>
/// 引擎路由 Provider：容器中唯一的 IDatabaseProvider，每个方法一行按 cfg.Engine
/// 从 ProviderRegistry 解析目标引擎 Provider 委托调用。引擎未注册时抛 DbpilotUnsupportedException
/// （采集侧 CollectRunner 捕获跳过、API 侧转明确报错）。Provider 实例从当前 scope 解析，
/// 同一请求内多次调用同引擎不缓存（Provider 无状态、连接按 cfg 即建即用）。
/// </summary>
public class RoutingDatabaseProvider(IServiceProvider sp, ProviderRegistry registry) : IDatabaseProvider
{
    private IDatabaseProvider For(InstanceConfig cfg) => registry.Resolve(sp, cfg.Engine);

    /// <summary>按实例引擎路由到对应 Provider 的连通性 + 权限自检能力。</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).TestConnectionAsync(cfg, ct);

    /// <summary>按实例引擎路由到对应 Provider 的版本 / vCores / 启动时间探测能力。</summary>
    public Task<InstanceMeta> ProbeAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).ProbeAsync(cfg, ct);

    /// <summary>按实例引擎路由到对应 Provider 的库列表查询能力。</summary>
    public Task<List<string>> GetDatabasesAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).GetDatabasesAsync(cfg, ct);

    /// <summary>按实例引擎路由到对应 Provider 的缺失索引建议能力。</summary>
    public Task<List<MissingIndexItem>> GetMissingIndexesAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => For(cfg).GetMissingIndexesAsync(cfg, dbName, ct);

    /// <summary>按实例引擎路由到对应 Provider 的索引使用率查询能力。</summary>
    public Task<List<IndexUsageItem>> GetIndexUsageAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => For(cfg).GetIndexUsageAsync(cfg, dbName, ct);

    /// <summary>按实例引擎路由到对应 Provider 的碎片扫描候选表清单能力。</summary>
    public Task<List<FragTableInfo>> GetFragmentTablesAsync(InstanceConfig cfg, string dbName, int minPages, CancellationToken ct = default)
        => For(cfg).GetFragmentTablesAsync(cfg, dbName, minPages, ct);

    /// <summary>按实例引擎路由到对应 Provider 的单表索引碎片采集能力。</summary>
    public Task<List<IndexFragmentItem>> GetIndexFragmentationAsync(InstanceConfig cfg, string dbName, int objectId, int minPages, CancellationToken ct = default)
        => For(cfg).GetIndexFragmentationAsync(cfg, dbName, objectId, minPages, ct);

    /// <summary>按实例引擎路由到对应 Provider 的实时 Top SQL 聚合能力。</summary>
    public Task<List<TopSqlRawRow>> GetTopSqlRealtimeAsync(InstanceConfig cfg, string db, TopSqlFilter? filter = null, CancellationToken ct = default)
        => For(cfg).GetTopSqlRealtimeAsync(cfg, db, filter, ct);

    /// <summary>按实例引擎路由到对应 Provider 的活动请求采样能力。</summary>
    public Task<List<ActiveRequestRow>> GetActiveRequestsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).GetActiveRequestsAsync(cfg, ct);

    /// <summary>按实例引擎路由到对应 Provider 的头阻塞者补查能力。</summary>
    public Task<List<HeadBlockerRow>> GetHeadBlockersAsync(InstanceConfig cfg, List<int> headSessionIds, CancellationToken ct = default)
        => For(cfg).GetHeadBlockersAsync(cfg, headSessionIds, ct);

    /// <summary>按实例引擎路由到对应 Provider 的会话锁资源查询能力。</summary>
    public Task<List<SessionLockRow>> GetSessionLocksAsync(InstanceConfig cfg, List<int> sessionIds, CancellationToken ct = default)
        => For(cfg).GetSessionLocksAsync(cfg, sessionIds, ct);

    /// <summary>按实例引擎路由到对应 Provider 的死锁事件增量读取能力。</summary>
    public Task<DeadlockReadResult> ReadDeadlockEventsAsync(InstanceConfig cfg, DeadlockCursor? cursor, CancellationToken ct = default)
        => For(cfg).ReadDeadlockEventsAsync(cfg, cursor, ct);

    /// <summary>按实例引擎路由到对应 Provider 的慢SQL采集会话保障能力。</summary>
    public Task EnsureSlowSqlCaptureAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).EnsureSlowSqlCaptureAsync(cfg, ct);

    /// <summary>按实例引擎路由到对应 Provider 的慢SQL事件增量读取能力。</summary>
    public Task<SlowSqlReadResult> PollSlowSqlAsync(InstanceConfig cfg, SlowSqlCursor? cursor, CancellationToken ct = default)
        => For(cfg).PollSlowSqlAsync(cfg, cursor, ct);

    /// <summary>按实例引擎路由到对应 Provider 的计划快照累计统计采集能力。</summary>
    public Task<List<QueryPlanRawRow>> GetQueryPlanStatsAsync(InstanceConfig cfg, TopSqlFilter? filter = null, CancellationToken ct = default)
        => For(cfg).GetQueryPlanStatsAsync(cfg, filter, ct);

    /// <summary>按实例引擎路由到对应 Provider 的计划 XML 批量抓取能力。</summary>
    public Task<Dictionary<string, string>> GetQueryPlanXmlsAsync(InstanceConfig cfg, List<QueryPlanHandleRef> handles, CancellationToken ct = default)
        => For(cfg).GetQueryPlanXmlsAsync(cfg, handles, ct);

    /// <summary>按实例引擎路由到对应 Provider 的实例性能指标快照采集能力。</summary>
    public Task<InstanceMetricsSnapshot> GetInstanceMetricsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).GetInstanceMetricsAsync(cfg, ct);

    /// <summary>按实例引擎路由到对应 Provider 的磁盘卷用量查询能力。</summary>
    public Task<List<InstanceDiskRawRow>> GetInstanceDiskUsageAsync(InstanceConfig cfg, CancellationToken ct = default)
        => For(cfg).GetInstanceDiskUsageAsync(cfg, ct);
}
