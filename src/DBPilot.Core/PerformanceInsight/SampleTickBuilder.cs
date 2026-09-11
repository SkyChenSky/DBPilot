using DBPilot.Common;
using DBPilot.Core.Blocking;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>
/// 一次 10s 采样的聚合快照：活跃样本数 + 等待桶计数 + 7 维度计数。
/// 活跃 = status ∈ running/runnable/suspended 且等待不在排除表。
/// </summary>
public class SampleTick
{
    /// <summary>采样时刻（UTC）。</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>活跃样本数（AAS 分子）。</summary>
    public int ActiveCount { get; set; }

    /// <summary>桶 → 样本数。</summary>
    public Dictionary<string, int> Buckets { get; set; } = [];

    /// <summary>维度 → 维度值 → 样本数（sql / wait / user / host / command / db / status）。</summary>
    public Dictionary<string, Dictionary<string, int>> Dims { get; set; } = [];
}

/// <summary>
/// 采样行 → SampleTick（平台侧纯函数）：桶映射 + 维度计数；SQL 维优先 query_hash，未命中用归一化文本指纹兜底。
/// </summary>
public static class SampleTickBuilder
{
    public const string DimSql = "sql";
    public const string DimWait = "wait";
    public const string DimUser = "user";
    public const string DimHost = "host";
    public const string DimCommand = "command";
    public const string DimDb = "db";
    public const string DimStatus = "status";

    /// <summary>联合维度："SQL 指纹|等待桶" 复合键 —— Load By SQL 的 AAS 构成展示（随 Dims JSON 落分钟表）。</summary>
    public const string DimSqlWait = "sqlWait";

    public static readonly string[] AllDims =
        [DimSql, DimWait, DimUser, DimHost, DimCommand, DimDb, DimStatus, DimSqlWait];

    /// <summary>活跃状态集合（AAS 统计口径：running / runnable / suspended；rollback 行仅供阻塞树）。</summary>
    private static readonly HashSet<string> ActiveStatus =
        ["running", "runnable", "suspended"];

    public static SampleTick Build(List<ActiveRequestRow> rows, DateTime timeUtc)
    {
        var tick = new SampleTick { TimeUtc = timeUtc };
        foreach (var r in rows)
        {
            var status = r.Status?.Trim().ToLowerInvariant() ?? "";
            if (!ActiveStatus.Contains(status)) continue;

            var bucket = WaitBuckets.BucketOf(status, r.WaitType);
            if (bucket == null || bucket == WaitBuckets.Idle)
                continue;   // idle（SLEEP 等引擎后台空闲）保留分类但不计 AAS；userWait（WAITFOR）计活跃

            tick.ActiveCount++;
            tick.Buckets[bucket] = tick.Buckets.GetValueOrDefault(bucket) + 1;

            var fp = SqlFingerprint(r);
            AddDim(tick, DimSql, fp);
            AddDim(tick, DimSqlWait, $"{fp}|{bucket}");
            AddDim(tick, DimWait, string.IsNullOrWhiteSpace(r.WaitType) ? "(running)" : r.WaitType.Trim());
            AddDim(tick, DimUser, r.LoginName ?? "-");
            AddDim(tick, DimHost, r.HostName ?? "-");
            AddDim(tick, DimCommand, r.Command ?? "-");
            AddDim(tick, DimDb, r.DbName ?? "-");
            AddDim(tick, DimStatus, status);
        }
        return tick;
    }

    private static void AddDim(SampleTick tick, string dim, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) value = "-";
        if (!tick.Dims.TryGetValue(dim, out var m))
            tick.Dims[dim] = m = [];
        m[value] = m.GetValueOrDefault(value) + 1;
    }

    /// <summary>SQL 指纹：query_hash（跨样本稳定）优先；未命中用归一化文本 SHA256 前 16 位兜底（供文本字典登记复用）。</summary>
    internal static string SqlFingerprint(ActiveRequestRow r)
    {
        if (!string.IsNullOrWhiteSpace(r.QueryHash)) return r.QueryHash.Trim().ToLowerInvariant();
        var normalized = (r.SqlText ?? "").NormalizeWhitespace().ToLowerInvariant();
        if (normalized.Length == 0) return "-";
        return "txt:" + normalized.Sha256PrefixHex();
    }
}
