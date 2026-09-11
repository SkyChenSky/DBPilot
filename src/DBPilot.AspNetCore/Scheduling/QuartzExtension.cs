using DBPilot.Core.Housekeeping;
using DBPilot.Core.PerformanceInsight;
using DBPilot.AspNetCore.Extension;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Serilog;

namespace DBPilot.AspNetCore.Scheduling;

/// <summary>
/// Quartz 调度注册：
/// 配置驱动 —— appsettings DBPilot:Jobs（Job 名 → cron）与内置默认 <see cref="DefaultJobCrons"/> 合并
/// （配置缺失 = 默认全集；覆盖默认值；显式 null/空串 = 禁用该 Job），只注册已实现的 Job，未实现的记 Info 跳过。
/// RAMJobStore（默认，不持久化不建表）；misfire 跳过不补跑（采样语义：过期 tick 无意义）。
/// 仅 Collector 角色注册（DBPilot:Roles:Collector=false 时整个调度器不启动）。
/// </summary>
public static class QuartzExtension
{
    /// <summary>配置 Job 名 → Job 类型（新 Job 在此登记，默认 cron 加进 DefaultJobCrons）。</summary>
    internal static readonly Dictionary<string, Type> JobTypes = new()
    {
        ["SessionSample"] = typeof(SessionSampleJob),
        ["SampleFlush"] = typeof(SampleFlushJob),
        ["TopSqlDelta"] = typeof(TopSqlDeltaJob),
        ["Deadlock"] = typeof(DeadlockJob),
        ["SlowSql"] = typeof(SlowSqlJob),
        ["Housekeeping"] = typeof(HousekeepingJob),
        ["IndexSnapshot"] = typeof(IndexSnapshotJob),
        ["QueryPlan"] = typeof(QueryPlanJob),
    };

    /// <summary>内置默认节奏（零配置可用；配置 DBPilot:Jobs 覆盖单项、置空禁用单项）。</summary>
    internal static readonly Dictionary<string, string> DefaultJobCrons = new()
    {
        ["SessionSample"] = "0/10 * * * * ?",
        ["SampleFlush"] = "0 * * * * ?",
        ["TopSqlDelta"] = "0 * * * * ?",
        ["Deadlock"] = "0 * * * * ?",
        ["SlowSql"] = "0/30 * * * * ?",
        ["Housekeeping"] = "0 30 2 * * ?",
        ["IndexSnapshot"] = "0 10 3 * * ?",
        ["QueryPlan"] = "0 */5 * * * ?",
        // InstanceMetrics 由 InstanceMetricsExtension 按模块开关动态登记类型，默认节奏在此一并内置
        ["InstanceMetrics"] = "0/10 * * * * ?",
    };

    /// <summary>
    /// 合并 Job cron（纯函数，供单测）：默认全集 ← 配置覆盖；未实现（不在 JobTypes）的配置项忽略；
    /// 配置显式 null/空白 = 从结果移除（禁用该 Job）。
    /// </summary>
    internal static Dictionary<string, string> MergeJobCrons(IDictionary<string, string?>? overrides)
    {
        var merged = new Dictionary<string, string>(DefaultJobCrons);
        if (overrides is null) return merged;

        foreach (var (name, cron) in overrides)
        {
            if (!JobTypes.ContainsKey(name))
            {
                Log.Information("DBPilot:Jobs 配置项 {Name} 无对应 Job 实现，忽略（检查拼写，或先在 JobTypes 登记）", name);
                continue;
            }
            if (string.IsNullOrWhiteSpace(cron))
            {
                merged.Remove(name);
                continue;
            }
            merged[name] = cron;
        }
        return merged;
    }

    /// <summary>Host 入口（启动模块化链式组合用）：调度底座单例 + Job 注册。</summary>
    public static WebApplicationBuilder AddDbpilotQuartz(this WebApplicationBuilder builder)
    {
        builder.Services.AddDbpilotQuartz(builder.Configuration);
        return builder;
    }

    /// <summary>模块 Job 动态登记（仅启动组装期调用，如 InstanceMetricsExtension 插件式开关）。</summary>
    public static void RegisterModuleJob(string name, Type type) => JobTypes[name] = type;

    public static IServiceCollection AddDbpilotQuartz(this IServiceCollection services, IConfiguration configuration)
    {
        // 纯组合无开关（是否调用由全家桶 AddDBPilot 或宿主组合根表达）。
        // 采集底座状态存储与配置对象（Collect/Retention/TopSqlExclude）由 AddDbpilotServices 注册
        // （服务类经标记接口扫描全量注册，配置对象须角色无关），此处只做 Job 注册
        var jobs = MergeJobCrons(configuration.GetSection(DbpilotConfigKeys.Jobs).Get<Dictionary<string, string?>>());
        var registered = new List<string>();

        services.AddQuartz(q =>
        {
            // JobFactory 默认即从 Microsoft DI 按 scoped 解析（Quartz ≥3.3.2），无需显式注册
            q.SchedulerId = "dbpilot-scheduler";
            q.SchedulerName = "DBPilot Scheduler";

            foreach (var (name, cron) in jobs)
            {
                // MergeJobCrons 已过滤未实现项，此处必命中
                var jobType = JobTypes[name];

                var key = new JobKey(name);
                q.AddJob(jobType, key);
                q.AddTrigger(t => t
                    .WithIdentity($"{name}Trigger")
                    .ForJob(key)
                    .WithCronSchedule(cron, c => c.WithMisfireHandlingInstructionDoNothing()));
                registered.Add(name);
            }
        });

        if (registered.Count > 0)
        {
            services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
            Log.Information("Quartz 调度器已注册 Job：{Jobs}", string.Join(", ", registered));
        }
        else
        {
            Log.Warning("DBPilot:Jobs 合并后无已实现的 Job，调度器未启动");
        }

        return services;
    }
}
