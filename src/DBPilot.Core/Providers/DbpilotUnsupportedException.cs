namespace DBPilot.Core.Providers;

/// <summary>
/// 引擎能力边界异常：两类来源共用——
/// ① 引擎未注册（RoutingDatabaseProvider 找不到 engine 对应 Provider，如只装了 SqlServer 却接入 MySQL 实例）；
/// ② 功能无对等数据源（如 MySQL 无死锁 XE 历史、无缺失索引 DMV，由各 Provider 显式抛出）。
/// 采集侧 CollectRunner 捕获后仅记 Debug 跳过（不计退避不 WARN）；API/MCP 侧文案统一「该功能不支持 XX 实例」。
/// </summary>
public class DbpilotUnsupportedException : Exception
{
    /// <summary>触发引擎（小写；未注册场景可能为实例配置的任意串）。</summary>
    public string Engine { get; }

    public DbpilotUnsupportedException(string engine, string message) : base(message)
    {
        Engine = engine;
    }

    /// <summary>引擎未注册（路由层）：报错附带已注册引擎清单与接入指引。</summary>
    public static DbpilotUnsupportedException NotRegistered(string? engine, IReadOnlyCollection<string> registered)
    {
        var known = string.Join("、", registered);
        return new DbpilotUnsupportedException(
            engine ?? string.Empty,
            $"未注册引擎「{engine ?? "(空)"}」（已注册：{(known.Length > 0 ? known : "无")}）。"
            + " 请为宿主引用对应引擎包（DBPilot.SqlServer / DBPilot.MySql / DBPilot.PostgreSql，引用即自动注册）。");
    }

    /// <summary>功能无对等数据源（Provider 层）：如「死锁事件读取不支持 MySQL 实例」。</summary>
    public static DbpilotUnsupportedException Feature(string engine, string feature)
        => new(engine, $"{feature}不支持 {engine} 实例（无对等数据源）");
}
