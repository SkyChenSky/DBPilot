using DBPilot.Core.InstanceMetrics;
using DBPilot.Storage.Entities;

namespace DBPilot.UnitTests.InstanceMetricsTests;

/// <summary>实例性能指标采集：计数器配对 / 差值率计算 / 负差值重启 / 磁盘使用率。</summary>
public class InstanceMetricsTests
{
    private static readonly DateTime T0 = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private static CounterRow Row(string name, long value) => new() { CounterName = name, CntrValue = value };

    [Fact]
    public void 命中率为比值计数器_value除以base换算百分比()
    {
        var snap = new InstanceMetricsSnapshot
        {
            Counters = [Row("Buffer cache hit ratio", 9_500), Row("Buffer cache hit ratio base", 10_000)],
        };

        var (_, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);

        Assert.Equal(95m, gauges.BufferCacheHitRatioPct);
    }

    [Fact]
    public void 计数器名大小写不一致_忽略大小写匹配()
    {
        // RDS 实测：实例返回 "Lazy writes/sec"（小写 w），文档名 "Lazy Writes/sec"
        var snap = new InstanceMetricsSnapshot
        {
            Counters = [Row("Lazy writes/sec", 33), Row("FULL SCANS/SEC", 120)],
        };

        var (cumulative, _) = InstanceMetricsCollectService.ResolveCounters(snap);

        Assert.Equal(33, cumulative.LazyWrites);
        Assert.Equal(120, cumulative.FullScans);
    }

    [Fact]
    public void base行缺失_命中率为null()
    {
        var snap = new InstanceMetricsSnapshot { Counters = [Row("Buffer cache hit ratio", 9_500)] };

        var (_, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);

        Assert.Null(gauges.BufferCacheHitRatioPct);
    }

    [Fact]
    public void 计数器行缺失_对应累计值为null且不抛异常()
    {
        var snap = new InstanceMetricsSnapshot
        {
            Counters = [Row("User Connections", 12), Row("Processes blocked", 2), Row("Page life expectancy", 3000)],
        };

        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);

        Assert.Null(cumulative.BatchRequests);
        Assert.Equal(12, gauges.UserConnections);
        Assert.Equal(2, gauges.BlockedProcesses);
        Assert.Equal(3000, gauges.Ple);
    }

    [Fact]
    public void 首拍无基线_率值全null_瞬时值照落()
    {
        var snap = new InstanceMetricsSnapshot
        {
            CpuUsagePct = 33.33m,
            OsTotalMemoryKb = 1000,
            OsAvailableMemoryKb = 250,
            SqlMemoryKb = 600,
        };
        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);

        var row = InstanceMetricsCollectService.ComposeRow(1, T0, snap, cumulative, gauges, null, null);

        Assert.Null(row.Qps);
        Assert.Null(row.IopsRead);
        Assert.Equal(33.33m, row.CpuUsagePct);
        Assert.Equal(75m, row.MemUsagePct);                       // (1000-250)/1000
        Assert.Equal(600, row.SqlMemoryKb);
        Assert.Equal(T0, row.SampleTime);
    }

    [Fact]
    public void 正常差值_率值等于差值除以间隔秒()
    {
        var snap = new InstanceMetricsSnapshot
        {
            Counters = [Row("Batch Requests/sec", 60_000), Row("Transactions/sec", 6_000)],
            IoReads = 12_000,
            IoBytesRead = 100L * 1048576,   // 100 MB
        };

        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);
        var previous = new MetricCounterValues
        {
            BatchRequests = 0,
            Transactions = 0,
            IoReads = 0,
            IoBytesRead = 0,
        };

        var row = InstanceMetricsCollectService.ComposeRow(1, T0.AddSeconds(60), snap, cumulative, gauges, previous, T0);

        Assert.Equal(1000m, row.Qps);       // 60000 / 60
        Assert.Equal(100m, row.Tps);        // 6000 / 60
        Assert.Equal(200m, row.IopsRead);   // 12000 / 60
        Assert.Equal(1.667m, row.MbpsRead); // 100MB / 60s
    }

    [Fact]
    public void 十秒差值窗口_率值等于差值除以10_语义与采集频率解耦()
    {
        var snap = new InstanceMetricsSnapshot
        {
            Counters = [Row("Batch Requests/sec", 10_000)],
            IoReads = 2_000,
        };

        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);
        var previous = new MetricCounterValues { BatchRequests = 0, IoReads = 0 };

        var row = InstanceMetricsCollectService.ComposeRow(1, T0.AddSeconds(10), snap, cumulative, gauges, previous, T0);

        Assert.Equal(1000m, row.Qps);      // 10000 / 10
        Assert.Equal(200m, row.IopsRead);  // 2000 / 10
    }

    [Fact]
    public void 负差值实例重启_本拍率值全部跳过()
    {
        var snap = new InstanceMetricsSnapshot
        {
            Counters = [Row("Batch Requests/sec", 100)],   // 重启后清零重累计 < 基线
            IoReads = 50,
        };
        var (cumulative, gauges) = InstanceMetricsCollectService.ResolveCounters(snap);
        var previous = new MetricCounterValues
        {
            BatchRequests = 60_000,
            IoReads = 12_000,
            IoWrites = 8_000,
        };

        var row = InstanceMetricsCollectService.ComposeRow(1, T0.AddSeconds(60), snap, cumulative, gauges, previous, T0);

        Assert.Null(row.Qps);
        Assert.Null(row.Tps);
        Assert.Null(row.IopsRead);
        Assert.Null(row.IopsWrite);
    }

    [Fact]
    public void 磁盘使用率_总量为零容错null()
    {
        Assert.Equal(75m, InstanceMetricsCollectService.UsedPct(1000, 250));
        Assert.Equal(0m, InstanceMetricsCollectService.UsedPct(1000, 1000));
        Assert.Null(InstanceMetricsCollectService.UsedPct(0, 100));
        Assert.Null(InstanceMetricsCollectService.UsedPct(null, 100));
    }
}

/// <summary>指标趋势分桶降采样：粒度自适应 / 桶起点对齐 / 空桶断点 / 均值计算。</summary>
public class MetricsBucketizeTests
{
    private static readonly DateTime From = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 桶粒度自适应_七天以上五分钟_三天以上两分钟_十二时以上一分钟_两时以上三十秒_其余十秒()
    {
        Assert.Equal(300, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromDays(10)));
        Assert.Equal(300, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromDays(7)));
        Assert.Equal(120, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromDays(5)));
        Assert.Equal(120, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromDays(3)));
        Assert.Equal(60, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromHours(24)));
        Assert.Equal(60, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromHours(12)));
        Assert.Equal(30, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromHours(6)));
        Assert.Equal(30, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromHours(2)));
        Assert.Equal(10, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromMinutes(30)));
        Assert.Equal(10, InstanceMetricsQueryService.AutoBucketSeconds(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void 桶起点对齐step_覆盖from到to()
    {
        // from 偏移 37s，5min 桶首起点应回退对齐到整 5min；10 分钟跨度 / 5min = 2 桶
        var buckets = InstanceMetricsQueryService.BuildBuckets(From.AddSeconds(37), From.AddMinutes(10), 300);

        Assert.Equal(From, buckets[0]);
        Assert.Equal(2, buckets.Count);
        Assert.Equal(From.AddMinutes(5), buckets[^1]);
    }

    [Fact]
    public void 按桶求均值_空桶为null断点_null样本不计入()
    {
        var buckets = InstanceMetricsQueryService.BuildBuckets(From, From.AddMinutes(3), 60);
        var rows = new List<(DateTime T, decimal? V)>
        {
            (From.AddSeconds(10), 10m),      // 第 1 桶
            (From.AddSeconds(70), 20m),      // 第 2 桶
            (From.AddSeconds(90), 40m),      // 第 2 桶（均值 30）
            (From.AddSeconds(130), null),    // 第 3 桶 null 样本不计入
            (From.AddHours(1), 99m),         // 范围外丢弃
        };

        var result = InstanceMetricsQueryService.AvgByBucket(
            rows, buckets, 60, x => x.T, x => x.V);

        Assert.Equal(3, result.Count);
        Assert.Equal(10m, result[0]);
        Assert.Equal(30m, result[1]);
        Assert.Null(result[2]);
    }

    [Fact]
    public void 空序列_全桶null()
    {
        var buckets = InstanceMetricsQueryService.BuildBuckets(From, From.AddMinutes(2), 60);

        var result = InstanceMetricsQueryService.AvgByBucket(
            Array.Empty<DbpilotInstanceMetrics>(), buckets, 60, x => x.SampleTime, x => x.CpuUsagePct);

        Assert.Equal(2, result.Count);
        Assert.All(result, v => Assert.Null(v));
    }
}
