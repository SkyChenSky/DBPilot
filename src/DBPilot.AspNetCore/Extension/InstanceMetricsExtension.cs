using DBPilot.AspNetCore.Scheduling;

namespace DBPilot.AspNetCore.Extension;

/// <summary>
/// 实例性能指标模块：向调度器登记 InstanceMetricsJob。
/// 无独立配置开关——模块级关停走全家桶 AddDBPilot(o =&gt; o.InstanceMetrics = false)，
/// 单 Job 级关停走 DBPilot:Jobs 置 null。业务服务仍经 IDepend 扫描注册（API 与前端页面始终可用，只是无新数据）。
/// </summary>
public static class InstanceMetricsExtension
{
    public static WebApplicationBuilder AddDbpilotInstanceMetrics(this WebApplicationBuilder builder)
    {
        QuartzExtension.RegisterModuleJob("InstanceMetrics", typeof(InstanceMetricsJob));
        return builder;
    }
}
