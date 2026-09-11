namespace DBPilot.Core.Providers;

/// <summary>平台库引擎枚举（DbpilotOptions.PlatformEngine 委托参数用；引擎名统一小写，路由匹配不区分大小写）。</summary>
public enum DbpilotEngine
{
    SqlServer,
    MySql,
    PostgreSql,

    /// <summary>仅平台库轴（嵌入式轻量部署）：无监控 Provider，不在 Known 白名单内。</summary>
    Sqlite,
}

/// <summary>引擎标识常量（dbpilot_instance.engine 列取值，统一小写；路由匹配不区分大小写）。</summary>
public static class DbpilotEngines
{
    public const string SqlServer = "sqlserver";
    public const string MySql = "mysql";
    public const string PostgreSql = "postgresql";

    /// <summary>仅平台库轴（嵌入式轻量部署），不可作被监控实例引擎。</summary>
    public const string Sqlite = "sqlite";

    /// <summary>实例表单可选择的已知引擎（新增引擎包时在此登记 + 前端下拉同步）。</summary>
    public static readonly string[] Known = [SqlServer, MySql, PostgreSql];

    /// <summary>枚举转引擎名（null 透传——供"委托未写回落配置"的合并逻辑）。</summary>
    public static string? ToName(DbpilotEngine? engine) => engine?.ToString().ToLowerInvariant();
}

/// <summary>
/// 功能键常量（引擎能力矩阵词汇）：字符串即守卫拒答文案里的功能名，由引擎包在
/// <see cref="DbpilotEngineAttribute.UnsupportedFeatures"/> 中声明不支持项，Core 据此判定不感知各引擎边界。
/// </summary>
public static class DbpilotFeatures
{
    public const string DeadlockEvents = "死锁事件读取";
    public const string QueryPlanSnapshot = "执行计划快照";
    public const string MissingIndex = "缺失索引建议";
    public const string Fragmentation = "碎片扫描";
    public const string SlowSqlXeChannel = "慢SQL XE 文件会话";
}

/// <summary>
/// Provider 的引擎标识（标在 IDatabaseProvider 实现类上）：AddDbpilotEngine&lt;TProvider&gt;()
/// 注册时读取，是 ProviderRegistry 的路由键。不用接口静态成员（static abstract 接口不能作 DI 泛型类型参数）。
/// UnsupportedFeatures 声明该引擎不支持的功能（<see cref="DbpilotFeatures"/> 键），留空 = 全支持。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DbpilotEngineAttribute(string engine) : Attribute
{
    public string Engine { get; } = engine;

    /// <summary>该引擎不支持的功能键集合（能力矩阵由引擎包自声明，新增引擎 Core 零改动）。</summary>
    public string[] UnsupportedFeatures { get; set; } = [];
}
