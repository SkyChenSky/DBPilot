using Chloe;
using DBPilot.Common;
using DBPilot.Core.Collecting;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.Collections.Concurrent;

namespace DBPilot.Core.InstanceMetrics;

/// <summary>
/// 实例性能指标采集（InstanceMetricsJob 每 10s 调用（cron 配置驱动），趋势页数据源）：
/// 一次快照 = 瞬时类（gauge：CPU/内存/PLE/命中率/连接/阻塞）直接落 +
/// 累计类（dm_os_performance_counters 计数器与 dm_io_virtual_file_stats IO）相邻两拍差值÷间隔秒落率值。
/// 差值语义与 TopSQL 对齐：① 首拍只建基率不落率值；② 负差值（实例重启计数器清零）→ 本拍率值全部跳过，
/// 基线同步为当前值。磁盘卷用量每卷一行独立落 dbpilot_instance_disk（2008 空集自然降级不落）。
/// 并发读被监控实例、平台库写串行（Chloe 上下文非线程安全）。
/// </summary>
public class InstanceMetricsCollectService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    InstanceMetricsBaselineStore baselines, CollectStateStore states, CollectOptions options) : IDepend
{
    public async Task CollectAllAsync(CancellationToken ct = default)
    {
        var entities = await CollectRunner.LoadEnabledAsync(sp);
        if (entities is null) return;
        var db = sp.GetRequiredService<DbContext>();

        var metrics = new ConcurrentBag<(int InstanceId, DateTime SampleTimeUtc, InstanceMetricsSnapshot Snapshot)>();
        var disks = new ConcurrentBag<(int InstanceId, DateTime SampleTimeUtc, List<InstanceDiskRawRow> Rows)>();

        await CollectRunner.ForEachEnabledAsync(entities, states, options, "实例指标采集", ct, async (e, token) =>
        {
            var state = states.Of(e.Id, "实例指标采集");
            var cfg = InstanceConfigResolver.ToConfig(crypto, e);
            var now = DateTime.UtcNow;
            var snapshot = await provider.GetInstanceMetricsAsync(cfg, token);
            state.OnSuccess();
            metrics.Add((e.Id, now, snapshot));

            var diskRows = await provider.GetInstanceDiskUsageAsync(cfg, token);
            if (diskRows.Count > 0)   // 2008 无 dm_os_volume_stats → 空集跳过（非故障）
                disks.Add((e.Id, now, diskRows));
        });

        // 平台库写串行：指标宽表 + 磁盘卷表 + 基线推进
        await CollectRunner.PersistAsync("实例指标落库失败", async () =>
        {
            foreach (var (instanceId, sampleTimeUtc, snapshot) in metrics)
                await PersistMetricsAsync(db, instanceId, sampleTimeUtc, snapshot);

            foreach (var (instanceId, sampleTimeUtc, rows) in disks)
                await PersistDisksAsync(db, instanceId, sampleTimeUtc, rows);
        });
    }

    private async Task PersistMetricsAsync(DbContext db, int instanceId, DateTime sampleTimeUtc, InstanceMetricsSnapshot snapshot)
    {
        var baseline = baselines.Of(instanceId);
        var (cumulative, gauges) = ResolveCounters(snapshot);

        var row = ComposeRow(instanceId, sampleTimeUtc, snapshot, cumulative, gauges,
            baseline.HasBaseline ? baseline.Values : null, baseline.HasBaseline ? baseline.SampleTimeUtc : null);
        await db.InsertAsync(row);

        // 基线推进（含首拍建基线 / 负差值同步为当前值）
        baseline.Values = cumulative;
        baseline.SampleTimeUtc = sampleTimeUtc;
        baseline.HasBaseline = true;
    }

    private static async Task PersistDisksAsync(DbContext db, int instanceId, DateTime sampleTimeUtc, List<InstanceDiskRawRow> rows)
    {
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.VolumeMountPoint)) continue;
            await db.InsertAsync(new DbpilotInstanceDisk
            {
                InstanceId = instanceId,
                VolumeMountPoint = r.VolumeMountPoint,
                TotalMb = r.TotalMb,
                AvailableMb = r.AvailableMb,
                UsedPct = UsedPct(r.TotalMb, r.AvailableMb),
                SampleTime = sampleTimeUtc,
            });
        }
    }

    /// <summary>磁盘使用率 %（total ≤ 0 容错 null）。</summary>
    internal static decimal? UsedPct(long? totalMb, long? availableMb)
    {
        if (totalMb is not > 0 || availableMb is null) return null;
        return Math.Round((totalMb.Value - availableMb.Value) * 100m / totalMb.Value, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 快照 → 累计值集合 + 瞬时值集合（纯函数，供单测）。
    /// Buffer cache hit ratio 为比值计数器：value/base×100 瞬时计算（base 行 instance_name 为空，
    /// SQL 端未做 instance 过滤，此处按 counter_name 配对）；IO 累计来自快照直查列（差值语义与计数器一致）。
    /// 计数器名匹配忽略大小写（实例实际名 "Lazy writes/sec" 与文档名大小写不一致，RDS 实测）。
    /// </summary>
    internal static (MetricCounterValues Cumulative, MetricGauges Gauges) ResolveCounters(InstanceMetricsSnapshot snapshot)
    {
        var rows = snapshot.Counters;
        long? Value(string name) => rows
            .Where(r => string.Equals(r.CounterName, name, StringComparison.OrdinalIgnoreCase))
            .Select(r => (long?)r.CntrValue)
            .FirstOrDefault();

        var hit = Value("Buffer cache hit ratio");
        var hitBase = Value("Buffer cache hit ratio base");
        decimal? hitPct = hit.HasValue && hitBase is > 0
            ? Math.Round(hit.Value * 100m / hitBase.Value, 2, MidpointRounding.AwayFromZero)
            : null;

        var cumulative = new MetricCounterValues
        {
            BatchRequests = Value("Batch Requests/sec"),
            Transactions = Value("Transactions/sec"),
            Logins = Value("Logins/sec"),
            Compilations = Value("SQL Compilations/sec"),
            Recompilations = Value("SQL Re-Compilations/sec"),
            FullScans = Value("Full Scans/sec"),
            LazyWrites = Value("Lazy Writes/sec"),
            Deadlocks = Value("Number of Deadlocks/sec"),
            LockTimeouts = Value("Lock Timeouts/sec"),
            LockWaits = Value("Lock Waits/sec"),
            IoReads = snapshot.IoReads,
            IoWrites = snapshot.IoWrites,
            IoBytesRead = snapshot.IoBytesRead,
            IoBytesWritten = snapshot.IoBytesWritten,
        };
        var gauges = new MetricGauges
        {
            Ple = (int?)Value("Page life expectancy"),
            BufferCacheHitRatioPct = hitPct,
            UserConnections = (int?)Value("User Connections"),
            BlockedProcesses = (int?)Value("Processes blocked"),
        };
        return (cumulative, gauges);
    }

    /// <summary>
    /// 组装落库行（纯函数，供单测）：瞬时类直接填；率类 = (当前累计 - 基线累计) ÷ 间隔秒。
    /// 任一累计对出现负差值（实例重启计数器清零）→ 本拍率值全部为 null，由调用方同步基线。
    /// </summary>
    internal static DbpilotInstanceMetrics ComposeRow(int instanceId, DateTime sampleTimeUtc, InstanceMetricsSnapshot snapshot,
        MetricCounterValues cumulative, MetricGauges gauges, MetricCounterValues? previous, DateTime? previousTimeUtc)
    {
        var seconds = previousTimeUtc is null ? 0 : (sampleTimeUtc - previousTimeUtc.Value).TotalSeconds;
        var restarted = false;
        if (previous != null && seconds > 0)
        {
            restarted = cumulative.BatchRequests < previous.BatchRequests
                || cumulative.Deadlocks < previous.Deadlocks
                || cumulative.IoReads < previous.IoReads;
        }

        decimal? Rate(long? cur, long? prev)
            => !restarted && previous != null && seconds > 0 && cur.HasValue && prev.HasValue
                ? Math.Round((cur.Value - prev.Value) / (decimal)seconds, 2, MidpointRounding.AwayFromZero)
                : null;

        decimal? MbpsRate(long? cur, long? prev)
            => !restarted && previous != null && seconds > 0 && cur.HasValue && prev.HasValue
                ? Math.Round((cur.Value - prev.Value) / 1048576m / (decimal)seconds, 3, MidpointRounding.AwayFromZero)
                : null;

        return new DbpilotInstanceMetrics
        {
            InstanceId = instanceId,
            SampleTime = sampleTimeUtc,
            CpuUsagePct = snapshot.CpuUsagePct,
            MemUsagePct = snapshot.OsTotalMemoryKb is > 0 && snapshot.OsAvailableMemoryKb != null
                ? Math.Round((snapshot.OsTotalMemoryKb.Value - snapshot.OsAvailableMemoryKb.Value) * 100m
                    / snapshot.OsTotalMemoryKb.Value, 2, MidpointRounding.AwayFromZero)
                : null,
            OsTotalMemoryKb = snapshot.OsTotalMemoryKb,
            OsAvailableMemoryKb = snapshot.OsAvailableMemoryKb,
            SqlMemoryKb = snapshot.SqlMemoryKb,
            Qps = Rate(cumulative.BatchRequests, previous?.BatchRequests),
            Tps = Rate(cumulative.Transactions, previous?.Transactions),
            LoginsPerSec = Rate(cumulative.Logins, previous?.Logins),
            CompilationsPerSec = Rate(cumulative.Compilations, previous?.Compilations),
            RecompilationsPerSec = Rate(cumulative.Recompilations, previous?.Recompilations),
            FullScansPerSec = Rate(cumulative.FullScans, previous?.FullScans),
            LazyWritesPerSec = Rate(cumulative.LazyWrites, previous?.LazyWrites),
            Ple = gauges.Ple,
            BufferCacheHitRatioPct = gauges.BufferCacheHitRatioPct,
            DeadlocksPerSec = Rate(cumulative.Deadlocks, previous?.Deadlocks),
            LockTimeoutsPerSec = Rate(cumulative.LockTimeouts, previous?.LockTimeouts),
            LockWaitsPerSec = Rate(cumulative.LockWaits, previous?.LockWaits),
            UserConnections = gauges.UserConnections,
            BlockedProcesses = gauges.BlockedProcesses,
            IopsRead = Rate(cumulative.IoReads, previous?.IoReads),
            IopsWrite = Rate(cumulative.IoWrites, previous?.IoWrites),
            MbpsRead = MbpsRate(cumulative.IoBytesRead, previous?.IoBytesRead),
            MbpsWrite = MbpsRate(cumulative.IoBytesWritten, previous?.IoBytesWritten),
        };
    }
}
