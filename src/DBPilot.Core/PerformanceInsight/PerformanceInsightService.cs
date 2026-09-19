using Chloe;
using DBPilot.Common;
using DBPilot.Core.Instances;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>
/// 性能洞察查询：区间 ≤60 分钟走内存环形缓冲（10s 粒度），更长走分钟聚合表；
/// AAS 分类切换（七维）与 Load By SQL / 单 SQL 趋势共用同一路径。
/// </summary>
public class PerformanceInsightService(IServiceProvider sp, SampleBufferRegistry registry) : IDepend
{
    public const int MaxRealtimeMinutes = 60;

    /// <summary>走内存缓冲的区间阈值：区间 &lt; 60 分钟用实时缓冲 10s 瞬时值，更长走分钟均值表。</summary>
    public const int RealtimeMinutes = 60;
    private const int TopSqlN = 10;

    /// <summary>实时 AAS：内存缓冲直出（minutes ≤ 60，默认 15）。</summary>
    public async Task<ServiceResult<AasRealtimeResult>> GetRealtimeAsync(int instanceId, int minutes = 15)
    {
        var (err, entity) = await InstanceConfigResolver.LoadAsync(sp, instanceId);
        if (err != null) return ServiceResult<AasRealtimeResult>.Failed(err);

        minutes = Math.Clamp(minutes, 1, MaxRealtimeMinutes);
        var now = DateTime.UtcNow;
        var buffer = registry.Of(instanceId);

        return ServiceResult<AasRealtimeResult>.Succeeded(new AasRealtimeResult
        {
            CpuCores = entity!.CpuCores,
            BucketNames = WaitBuckets.DisplayNames,
            LastTickUtc = buffer.LastTickUtc,
            Points = buffer.Range(now.AddMinutes(-minutes), now)
                .Select(t => new AasPoint
                {
                    TimeUtc = t.TimeUtc,
                    Active = t.ActiveCount,
                    Buckets = t.Buckets.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value),
                    Dims = t.Dims.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ToDictionary(k => k.Key, k => (decimal)k.Value)),
                })
                .ToList(),
        });
    }

    /// <summary>历史 AAS：分钟聚合表（start/end 为 UTC；无参默认近 24h）。</summary>
    public async Task<ServiceResult<AasHistoryResult>> GetHistoryAsync(int instanceId, DateTime? start, DateTime? end)
    {
        var rows = await QueryMinutesAsync(instanceId, start, end);
        if (rows.err != null) return ServiceResult<AasHistoryResult>.Failed(rows.err);
        var (_, entity) = await InstanceConfigResolver.LoadAsync(sp, instanceId);

        return ServiceResult<AasHistoryResult>.Succeeded(new AasHistoryResult
        {
            CpuCores = entity?.CpuCores,
            BucketNames = WaitBuckets.DisplayNames,
            Points = rows.list.Select(r => new AasHistoryPoint
            {
                TimeUtc = r.MinuteTime,
                Active = r.AvgActiveSessions,
                MaxActive = r.MaxActiveSessions,
                SampleCount = r.SampleCount,
                Buckets = ParseBuckets(r.Buckets),
                Dims = ParseDims(r.Dims),
            }).ToList(),
        });
    }

    /// <summary>
    /// Load By SQL：区间 &lt; 60 分钟走内存缓冲（10s 粒度），否则分钟表。
    /// 排除规则对齐 Top SQL 页：RDS/平台标记前缀 + 配置 LIKE 模式（patterns 由 Controller 从配置构建）
    /// + 指纹黑名单 dbpilot_top_sql_exclusion（本方法内部自动加载）。
    /// </summary>
    public async Task<ServiceResult<List<TopSqlItem>>> GetTopSqlAsync(int instanceId, DateTime? start, DateTime? end, List<string>? patterns = null)
    {
        var s = await CollectDimSeriesAsync(instanceId, SampleTickBuilder.DimSql, start, end);
        if (s.Err != null) return ServiceResult<List<TopSqlItem>>.Failed(s.Err);
        // 联合维度（指纹|桶）→ 每 SQL 的 AAS 构成
        var w = await CollectDimSeriesAsync(instanceId, SampleTickBuilder.DimSqlWait, start, end);
        if (w.Err != null) return ServiceResult<List<TopSqlItem>>.Failed(w.Err);

        var maps = s.Maps;
        var excludedFps = await LoadExcludedFingerprintsAsync();
        var likePatterns = patterns ?? [];

        // 候选取宽（TopSqlN×3）再过滤截断 —— 噪音挤掉的不进榜
        var candidates = AasDimMath.TopN(maps, TopSqlN * 3);
        var texts = await ResolveSqlTextsAsync(instanceId, candidates.Select(t => t.Key).ToList());
        var top = candidates
            .Where(t => !SqlNoiseFilter.IsExcluded(t.Key, texts.GetValueOrDefault(t.Key), excludedFps, likePatterns))
            .Take(TopSqlN)
            .ToList();

        // 占比分母 = 展示的 Top10 自身合计（对齐 Top SQL 页：TopN 变化占比随之变化）
        var total = top.Sum(t => t.Aas);
        return ServiceResult<List<TopSqlItem>>.Succeeded(
            top.Select(t => new TopSqlItem
            {
                Fingerprint = t.Key,
                Aas = t.Aas,
                Percent = total == 0 ? 0 : Math.Round(t.Aas / total * 100, 1),
                Buckets = BucketBreakdown(w.Maps, t.Key),
                SqlText = texts.GetValueOrDefault(t.Key),
            }).ToList());
    }

    /// <summary>sqlWait 联合维度（"指纹|桶"）→ 指定指纹的桶构成（区间均值，降序）。</summary>
    private static Dictionary<string, decimal> BucketBreakdown(List<Dictionary<string, decimal>> maps, string fingerprint)
    {
        if (maps.Count == 0) return [];
        var prefix = fingerprint + "|";
        var sums = new Dictionary<string, decimal>();
        foreach (var m in maps)
            foreach (var (key, value) in m)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var bucket = key[(prefix.Length)..];
                sums[bucket] = sums.GetValueOrDefault(bucket) + value;
            }
        return sums.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / maps.Count, 3));
    }

    /// <summary>单条 SQL 的 AAS 趋势：与 Load By SQL 同一路径，返回逐点贡献序列。</summary>
    public async Task<ServiceResult<SqlTrendResult>> GetSqlTrendAsync(int instanceId, string fingerprint, DateTime? start, DateTime? end)
    {
        fingerprint = fingerprint.Trim().ToLowerInvariant();
        var s = await CollectDimSeriesAsync(instanceId, SampleTickBuilder.DimSql, start, end);
        if (s.Err != null) return ServiceResult<SqlTrendResult>.Failed(s.Err);

        var series = AasDimMath.Series(s.Maps, fingerprint);
        var texts = await ResolveSqlTextsAsync(instanceId, [fingerprint]);
        return ServiceResult<SqlTrendResult>.Succeeded(new SqlTrendResult
        {
            Fingerprint = fingerprint,
            SqlText = texts.GetValueOrDefault(fingerprint),
            Granularity = s.Granularity,
            Points = s.Times.Zip(series, (t, v) => new SqlTrendPoint { TimeUtc = t, Value = v }).ToList(),
        });
    }

    /// <summary>收集区间内某维的逐点映射（区间 &lt; 1h 走内存缓冲 10s 瞬时值，≥ 1h 走分钟均值表；缓冲起点须在 60min 容量内）。</summary>
    private async Task<DimSeries> CollectDimSeriesAsync(int instanceId, string dim, DateTime? start, DateTime? end)
    {
        var to = end ?? DateTime.UtcNow;
        var from = start ?? to.AddMinutes(-15);
        if (from >= to) return new("start 必须早于 end", [], [], "10s");

        var buffer = registry.Of(instanceId);
        var useBuffer = (to - from).TotalMinutes < RealtimeMinutes
                        && from >= DateTime.UtcNow.AddMinutes(-MaxRealtimeMinutes);

        if (useBuffer)
        {
            var ticks = buffer.Range(from, to);
            return new(null,
                [.. ticks.Select(t => t.TimeUtc)],
                [.. ticks.Select(t => (t.Dims.GetValueOrDefault(dim) ?? [])
                    .ToDictionary(kv => kv.Key, kv => (decimal)kv.Value))],
                "10s");
        }

        var rows = await QueryMinutesAsync(instanceId, from, to);
        if (rows.err != null) return new(rows.err, [], [], "1min");
        return new(null,
            [.. rows.list.Select(r => r.MinuteTime)],
            [.. rows.list.Select(r => ParseDims(r.Dims).GetValueOrDefault(dim) ?? [])],
            "1min");
    }

    private sealed record DimSeries(string? Err, List<DateTime> Times, List<Dictionary<string, decimal>> Maps, string Granularity);

    private async Task<(string? err, List<DbpilotActiveRequestSample> list)> QueryMinutesAsync(int instanceId, DateTime? start, DateTime? end)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return (InstanceConfigResolver.DbNotConfigured, []);

        var (err, _) = await InstanceConfigResolver.LoadAsync(sp, instanceId);
        if (err != null) return (err, []);

        var to = end ?? DateTime.UtcNow;
        var from = start ?? to.AddHours(-24);
        if (from >= to) return ("start 必须早于 end", []);

        var list = await db.Query<DbpilotActiveRequestSample>()
            .Where(x => x.InstanceId == instanceId && x.MinuteTime >= from && x.MinuteTime < to)
            .OrderBy(x => x.MinuteTime)
            .ToListAsync();
        return (null, list);
    }

    /// <summary>指纹黑名单（dbpilot_top_sql_exclusion，与 Top SQL 页共用，全局生效）。</summary>
    private async Task<HashSet<string>> LoadExcludedFingerprintsAsync()
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return [];
        var rows = await db.Query<DbpilotTopSqlExclusion>().ToListAsync();
        return rows.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>指纹 → 文本：内存字典优先（首见文本，含未落库新指纹），缺的查 dbpilot_sql_template。</summary>
    private async Task<Dictionary<string, string>> ResolveSqlTextsAsync(int instanceId, List<string> fingerprints)
    {
        var result = new Dictionary<string, string>();
        var buffer = registry.Of(instanceId);
        var missing = new List<string>();

        foreach (var fp in fingerprints)
        {
            if (buffer.SqlTemplates.TryGetValue(fp, out var e)) result[fp] = e.SqlText;
            else missing.Add(fp);
        }
        if (missing.Count == 0) return result;

        var db = sp.GetService<DbContext>();
        if (db is null) return result;
        foreach (var fp in missing)
        {
            var row = await db.Query<DbpilotSqlTemplate>()
                .Where(x => x.InstanceId == instanceId && x.Fingerprint == fp)
                .FirstOrDefaultAsync();
            if (row != null) result[fp] = row.SqlText;
        }
        return result;
    }

    /// <summary>buckets JSON → 字典（统一 SerializeExtension 口径；字典键反序列化 verbatim，存量数据兼容；
    /// 历史脏数据容错：解析失败返回空）。</summary>
    internal static Dictionary<string, decimal> ParseBuckets(string json)
    {
        try
        {
            return json.FromJson<Dictionary<string, decimal>>() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>dims JSON（{"sql":[{"key":..,"value":..}]}）→ 字典（容错同上）。</summary>
    internal static Dictionary<string, Dictionary<string, decimal>> ParseDims(string json)
    {
        try
        {
            var raw = json.FromJson<Dictionary<string, List<DimEntry>>>() ?? [];
            return raw.ToDictionary(
                kv => kv.Key,
                kv => (kv.Value ?? [])
                    .Where(e => e.Key != null)
                    .GroupBy(e => e.Key!)                       // TopN 截断后不应重复，GroupBy 兜底
                    .ToDictionary(g => g.Key!, g => g.Sum(e => e.Value)));
        }
        catch
        {
            return [];
        }
    }

    private class DimEntry
    {
        [JsonProperty("key")] public string? Key { get; set; }
        [JsonProperty("value")] public decimal Value { get; set; }
    }
}
