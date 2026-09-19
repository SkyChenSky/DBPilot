 using DBPilot.Common;
using Chloe;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DBPilot.Core.Housekeeping;

/// <summary>
/// 清理保留期配置（appsettings Retention 节，单位天；0 = 该表不清理）。
/// 默认容量口径：分钟聚合/TopSQL 差值 35 天，慢SQL/阻塞 90 天，死锁 180 天。
/// </summary>
public class HousekeepingOptions
{
    public int ActiveRequestSampleDays { get; set; } = 35;
    public int TopSqlDeltaDays { get; set; } = 35;
    public int SlowSqlDays { get; set; } = 90;
    public int BlockingDays { get; set; } = 90;
    public int DeadlockDays { get; set; } = 180;
    public int MissingIndexSnapshotDays { get; set; } = 90;
    public int IndexUsageSnapshotDays { get; set; } = 90;
    public int QueryPlanDays { get; set; } = 90;
    public int PlanChangeDays { get; set; } = 90;
    public int InstanceMetricsDays { get; set; } = 30;
    public int InstanceDiskDays { get; set; } = 30;

    /// <summary>单批删除行数（DELETE TOP(N) 分批防大事务锁；节内可覆写）。</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>单次运行每表批数上限（防一次清理拖垮平台库，余量下次继续）。</summary>
    public int MaxBatchesPerTable { get; set; } = 200;
}

/// <summary>
/// 历史数据清理（HousekeepingJob 每日 02:30 调用）：
/// 十三张时序表按保留期滚动删除，分批循环直至不足一批（方言语句走 IPlatformDialect）；
/// SQL 文本字典 / 排除黑名单等维表不清理（长期有效）。
/// </summary>
public class HousekeepingService(IServiceProvider sp, HousekeepingOptions options, IPlatformDialect dialect) : IDepend
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return;

        // (表名, 时间列, 保留天数) —— 表名为内部常量，无注入面
        var plans = new (string Table, string Column, int Days)[]
        {
            ("dbpilot_active_request_sample", "minute_time", options.ActiveRequestSampleDays),
            ("dbpilot_top_sql_delta", "window_end", options.TopSqlDeltaDays),
            ("dbpilot_slow_sql", "event_time", options.SlowSqlDays),
            ("dbpilot_blocking_event", "start_time", options.BlockingDays),
            ("dbpilot_deadlock_event", "event_time", options.DeadlockDays),
            ("dbpilot_deadlock_process", "event_time", options.DeadlockDays),
            ("dbpilot_deadlock_resource", "event_time", options.DeadlockDays),
            ("dbpilot_missing_index_snapshot", "snapshot_time", options.MissingIndexSnapshotDays),
            ("dbpilot_index_usage_snapshot", "snapshot_time", options.IndexUsageSnapshotDays),
            ("dbpilot_query_plan", "last_seen_utc", options.QueryPlanDays),
            ("dbpilot_plan_change", "changed_at_utc", options.PlanChangeDays),
            ("dbpilot_instance_metrics", "sample_time", options.InstanceMetricsDays),
            ("dbpilot_instance_disk", "sample_time", options.InstanceDiskDays),
        };

        foreach (var (table, column, days) in plans)
        {
            if (days <= 0) continue;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var total = 0;
            for (var i = 0; i < options.MaxBatchesPerTable; i++)
            {
                ct.ThrowIfCancellationRequested();
                var affected = db.SqlQuery<int>(dialect.HousekeepingDeleteBatchSql(table, column, options.BatchSize),
                    new { cutoff }).FirstOrDefault();
                total += affected;
                if (affected < options.BatchSize) break;   // 不足一批 = 该表清完
            }
            if (total > 0)
                Log.Information("Housekeeping：{Table} 清理 {Days} 天前数据 {Count} 行", table, days, total);
        }
    }
}
