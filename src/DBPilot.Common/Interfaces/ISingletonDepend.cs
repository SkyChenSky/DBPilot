namespace DBPilot.Common;

/// <summary>
/// 依赖注入标记接口（单例）：实现类由 AddDbpilotServices 扫描后自动注册为 Singleton。
/// 用于无参构造的进程内状态存储（环形缓冲 / 退避状态 / XE 游标 / 差值基线等）；
/// 带配置的对象（CollectOptions / TopSqlExcludeOptions 等）仍手工注册。
/// 同时实现 IDepend 时以 Singleton 为准（生命周期取最长）。
/// </summary>
public interface ISingletonDepend
{
}
