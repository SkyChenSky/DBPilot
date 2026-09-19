using Chloe;
using DBPilot.Common;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.InstanceMetrics;

/// <summary>指标趋势列式序列（前端图表直用；空桶为 null 形成断点，不做补零）。</summary>
public class MetricsTrend
{
    public int BucketSeconds { get; set; }
    public List<DateTime> Times { get; set; } = [];
    public List<decimal?> CpuUsagePct { get; set; } = [];
    public List<decimal?> MemUsagePct { get; set; } = [];
    public List<decimal?> Qps { get; set; } = [];
    public List<decimal?> Tps { get; set; } = [];
    public List<decimal?> LoginsPerSec { get; set; } = [];
    public List<decimal?> CompilationsPerSec { get; set; } = [];
    public List<decimal?> RecompilationsPerSec { get; set; } = [];
    public List<decimal?> FullScansPerSec { get; set; } = [];
    public List<decimal?> LazyWritesPerSec { get; set; } = [];
    public List<decimal?> Ple { get; set; } = [];
    public List<decimal?> BufferCacheHitRatioPct { get; set; } = [];
    public List<decimal?> DeadlocksPerSec { get; set; } = [];
    public List<decimal?> LockTimeoutsPerSec { get; set; } = [];
    public List<decimal?> LockWaitsPerSec { get; set; } = [];
    public List<decimal?> UserConnections { get; set; } = [];
    public List<decimal?> BlockedProcesses { get; set; } = [];
    public List<decimal?> IopsRead { get; set; } = [];
    public List<decimal?> IopsWrite { get; set; } = [];
    public List<decimal?> MbpsRead { get; set; } = [];
    public List<decimal?> MbpsWrite { get; set; } = [];
}

/// <summary>磁盘使用率趋势（每卷一条序列）。</summary>
public class DiskUsageTrend
{
    public int BucketSeconds { get; set; }
    public List<DiskVolumeSeries> Volumes { get; set; } = [];
}

public class DiskVolumeSeries
{
    public string VolumeMountPoint { get; set; } = string.Empty;
    public List<DateTime> Times { get; set; } = [];
    public List<decimal?> UsedPct { get; set; } = [];
}

/// <summary>
/// 指标趋势查询：服务端按桶聚合降采样（桶内 avg，空桶 null 断点）——
/// 30 天原始 60s 点约 4.3 万，直接返回会卡前端。桶粒度 bucketSeconds 缺省按跨度自适应（五档，见 AutoBucketSeconds）。
/// </summary>
public class InstanceMetricsQueryService(IServiceProvider sp) : IDepend
{
    /// <summary>实例指标趋势（列式序列，全部指标共用时间轴）。</summary>
    public async Task<MetricsTrend?> GetTrendAsync(int instanceId, DateTime from, DateTime to, int? bucketSeconds)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var rows = await db.Query<DbpilotInstanceMetrics>()
            .Where(x => x.InstanceId == instanceId && x.SampleTime >= from && x.SampleTime < to)
            .OrderBy(x => x.SampleTime)
            .ToListAsync();

        var step = bucketSeconds is > 0 ? bucketSeconds.Value : AutoBucketSeconds(to - from);
        var buckets = BuildBuckets(from, to, step);

        List<decimal?> Series(Func<DbpilotInstanceMetrics, decimal?> selector)
            => AvgByBucket(rows, buckets, step, x => x.SampleTime, selector);

        return new MetricsTrend
        {
            BucketSeconds = step,
            Times = buckets,
            CpuUsagePct = Series(x => x.CpuUsagePct),
            MemUsagePct = Series(x => x.MemUsagePct),
            Qps = Series(x => x.Qps),
            Tps = Series(x => x.Tps),
            LoginsPerSec = Series(x => x.LoginsPerSec),
            CompilationsPerSec = Series(x => x.CompilationsPerSec),
            RecompilationsPerSec = Series(x => x.RecompilationsPerSec),
            FullScansPerSec = Series(x => x.FullScansPerSec),
            LazyWritesPerSec = Series(x => x.LazyWritesPerSec),
            Ple = Series(x => x.Ple),
            BufferCacheHitRatioPct = Series(x => x.BufferCacheHitRatioPct),
            DeadlocksPerSec = Series(x => x.DeadlocksPerSec),
            LockTimeoutsPerSec = Series(x => x.LockTimeoutsPerSec),
            LockWaitsPerSec = Series(x => x.LockWaitsPerSec),
            UserConnections = Series(x => x.UserConnections),
            BlockedProcesses = Series(x => x.BlockedProcesses),
            IopsRead = Series(x => x.IopsRead),
            IopsWrite = Series(x => x.IopsWrite),
            MbpsRead = Series(x => x.MbpsRead),
            MbpsWrite = Series(x => x.MbpsWrite),
        };
    }

    /// <summary>磁盘使用率趋势：范围内出现过的每卷一条序列（卷消失后不再有采样点）。</summary>
    public async Task<DiskUsageTrend?> GetDiskTrendAsync(int instanceId, DateTime from, DateTime to)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var rows = await db.Query<DbpilotInstanceDisk>()
            .Where(x => x.InstanceId == instanceId && x.SampleTime >= from && x.SampleTime < to)
            .OrderBy(x => x.SampleTime)
            .ToListAsync();

        var step = AutoBucketSeconds(to - from);
        var buckets = BuildBuckets(from, to, step);

        return new DiskUsageTrend
        {
            BucketSeconds = step,
            Volumes = rows.GroupBy(x => x.VolumeMountPoint)
                .OrderBy(g => g.Key)
                .Select(g => new DiskVolumeSeries
                {
                    VolumeMountPoint = g.Key,
                    Times = buckets,
                    UsedPct = AvgByBucket(g.ToList(), buckets, step, x => x.SampleTime, x => x.UsedPct),
                })
                .ToList(),
        };
    }

    /// <summary>桶粒度自适应：≥7 天→5min、≥3 天→2min、≥12h→60s、≥2h→30s、其余→10s（点数约 720~4320，控制前端体积）。</summary>
    internal static int AutoBucketSeconds(TimeSpan span)
        => span.TotalDays >= 7 ? 300
         : span.TotalDays >= 3 ? 120
         : span.TotalHours >= 12 ? 60
         : span.TotalHours >= 2 ? 30
         : 10;

    /// <summary>桶起点序列：首桶对齐 step、覆盖 [from, to)。</summary>
    internal static List<DateTime> BuildBuckets(DateTime from, DateTime to, int stepSeconds)
    {
        var step = TimeSpan.FromSeconds(Math.Max(1, stepSeconds));
        var start = from.AddTicks(-(from.Ticks % step.Ticks));
        var buckets = new List<DateTime>();
        for (var t = start; t < to; t += step)
            buckets.Add(t);
        return buckets;
    }

    /// <summary>按桶求均值（纯函数，供单测）：null 样本不参与均值，桶内无样本 → null 断点；范围外丢弃。</summary>
    internal static List<decimal?> AvgByBucket<T>(IReadOnlyList<T> rows, IReadOnlyList<DateTime> buckets, int stepSeconds,
        Func<T, DateTime> timeOf, Func<T, decimal?> valueOf)
    {
        var step = TimeSpan.FromSeconds(Math.Max(1, stepSeconds));
        var sums = new (decimal Sum, int Count)?[buckets.Count];
        var index = new Dictionary<DateTime, int>(buckets.Count);
        for (var i = 0; i < buckets.Count; i++)
            index[buckets[i]] = i;

        foreach (var r in rows)
        {
            var v = valueOf(r);
            if (v is null) continue;

            var key = timeOf(r).AddTicks(-(timeOf(r).Ticks % step.Ticks));
            if (!index.TryGetValue(key, out var i)) continue;   // 范围外丢弃

            sums[i] = sums[i] is { } s ? (s.Sum + v.Value, s.Count + 1) : (v.Value, 1);
        }

        return sums.Select(s => s is { } v && v.Count > 0
            ? (decimal?)Math.Round(v.Sum / v.Count, 2, MidpointRounding.AwayFromZero)
            : null)
            .ToList();
    }
}
