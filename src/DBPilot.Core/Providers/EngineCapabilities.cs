namespace DBPilot.Core.Providers;

/// <summary>引擎能力级别（UI 减法/降级形态驱动；full=完整功能，degraded=同页降级形态，none=隐藏入口）。</summary>
public enum DbpilotCapabilityLevel
{
    None = 0,
    Degraded = 1,
    Full = 2,
}

/// <summary>
/// UI 能力键常量（引擎能力矩阵词汇，/api/engines 返回给前端做减法/降级展示）。
/// 与 <see cref="DbpilotFeatures"/> 分工：后者是后端守卫拒答文案（中文功能名），
/// 本表是展示层粒度（含"趋势-only / 模板榜"这类同页降级形态），两者独立演进。
/// </summary>
public static class DbpilotCapabilityKeys
{
    /// <summary>死锁事件明细 + 资源类型趋势（dbpilot_deadlock_event）</summary>
    public const string DeadlockEvents = "deadlockEvents";

    /// <summary>死锁趋势（dbpilot_instance_metrics 计数器聚合；deadlockEvents 降级为 none 时的同页趋势形态）</summary>
    public const string DeadlockTrend = "deadlockTrend";

    public const string QueryPlanSnapshot = "queryPlanSnapshot";
    public const string MissingIndex = "missingIndex";
    public const string Fragmentation = "fragmentation";

    /// <summary>慢SQL事件明细（dbpilot_slow_sql）</summary>
    public const string SlowSqlEvents = "slowSqlEvents";

    /// <summary>慢SQL模板榜（dbpilot_top_sql_delta 聚合；slowSqlEvents 降级为 none 时的同页模板榜形态）</summary>
    public const string SlowSqlTemplates = "slowSqlTemplates";

    /// <summary>OS 级 CPU / 内存利用率（性能趋势页 CPU/内存卡）</summary>
    public const string OsCpuMem = "osCpuMem";

    /// <summary>PLE 页平均生存期</summary>
    public const string Ple = "ple";

    /// <summary>编译 / 重编译计数</summary>
    public const string CompileStats = "compileStats";

    /// <summary>阻塞进程数计数器（连接数卡的阻塞线）</summary>
    public const string BlockedProcesses = "blockedProcesses";

    /// <summary>未使用索引禁用脚本（INVISIBLE / DISABLE）</summary>
    public const string IndexDisableScript = "indexDisableScript";

    /// <summary>全部能力键（/api/engines 全矩阵输出用：已注册引擎未声明的键按 Full 补齐）</summary>
    public static readonly string[] All =
    [
        DeadlockEvents, DeadlockTrend, QueryPlanSnapshot, MissingIndex, Fragmentation,
        SlowSqlEvents, SlowSqlTemplates, OsCpuMem, Ple, CompileStats, BlockedProcesses, IndexDisableScript,
    ];
}

/// <summary>
/// 引擎 UI 能力自声明（标在 IDatabaseProvider 实现类上，与 [DbpilotEngine] 同源同生命周期）：
/// None / Degraded 为该键的降级清单，未列出的键默认 Full。经 ProviderRegistry 收集，
/// /api/engines 输出全引擎矩阵；前端不再硬编码引擎名。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DbpilotCapabilitiesAttribute : Attribute
{
    /// <summary>该引擎完全不具备的能力键（入口隐藏 / 整页空态）。</summary>
    public string[] None { get; set; } = [];

    /// <summary>该引擎以同页降级形态提供的能力键（趋势-only / 模板榜等）。</summary>
    public string[] Degraded { get; set; } = [];
}
