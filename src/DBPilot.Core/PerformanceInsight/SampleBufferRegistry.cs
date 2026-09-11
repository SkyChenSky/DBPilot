using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Blocking;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>待落库的 SQL 模板（内存字典条目：指纹 → 首见文本）。</summary>
public class SqlTemplateEntry
{
    public string Fingerprint { get; init; } = string.Empty;
    public string SqlText { get; init; } = string.Empty;
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>已写入 dbpilot_sql_template（SampleFlush 判重后置位；重启回内存态，重写一次幂等）。</summary>
    public volatile bool Persisted;
}

/// <summary>
/// 采样环形缓冲注册表：每实例一个 10s×360 = 60 分钟环形缓冲，单例（Job 写 / API 读）。
/// </summary>
public class SampleBufferRegistry : ISingletonDepend
{
    private readonly object _gate = new();
    private readonly Dictionary<int, InstanceSampleBuffer> _buffers = [];

    public InstanceSampleBuffer Of(int instanceId)
    {
        lock (_gate)
            return _buffers.TryGetValue(instanceId, out var b) ? b
                : _buffers[instanceId] = new InstanceSampleBuffer();
    }

    /// <summary>已有缓冲的实例 id（落库 Job 遍历用）。</summary>
    public List<int> InstanceIds
    {
        get { lock (_gate) return [.. _buffers.Keys]; }
    }
}

/// <summary>单实例采样环形缓冲（60 分钟，线程安全）。</summary>
public class InstanceSampleBuffer
{
    public const int Capacity = 360;   // 10s × 360 = 60min

    private readonly SampleTick?[] _slots = new SampleTick?[Capacity];
    private int _head;
    private readonly object _gate = new();

    /// <summary>已落库的分钟（UTC 整分），防重复写（重启丢失可接受 —— 内存缓冲语义）。</summary>
    private readonly HashSet<DateTime> _flushedMinutes = [];

    /// <summary>SQL 指纹 → 语句文本（Load By SQL 显示用；首见文本优先，容量 MaxSqlTemplates）。</summary>
    public ConcurrentDictionary<string, SqlTemplateEntry> SqlTemplates { get; } = new();

    /// <summary>内存 SQL 字典容量上限（超出不再新增 —— 防 long tail 语句撑爆内存）。</summary>
    public const int MaxSqlTemplates = 2000;

    public void Add(SampleTick tick)
    {
        lock (_gate)
        {
            _slots[_head] = tick;
            _head = (_head + 1) % Capacity;
        }
    }

    /// <summary>采样成功后登记 SQL 文本字典（指纹口径与 SampleTickBuilder 一致；语句级文本缺失兜底整批文本）。</summary>
    public void RememberSqlTexts(List<ActiveRequestRow> rows)
    {
        foreach (var r in rows)
        {
            var text = !string.IsNullOrWhiteSpace(r.SqlText) ? r.SqlText : r.BatchSqlText;
            if (string.IsNullOrWhiteSpace(text)) continue;   // RDS 受限查询/系统会话：文本天然不可得
            var fp = SampleTickBuilder.SqlFingerprint(r);
            if (fp == "-") continue;
            if (SqlTemplates.TryGetValue(fp, out _)) continue;
            if (SqlTemplates.Count >= MaxSqlTemplates) return;
            SqlTemplates[fp] = new SqlTemplateEntry
            {
                Fingerprint = fp,
                SqlText = text,
                FirstSeenUtc = DateTime.UtcNow,
            };
        }
    }

    /// <summary>时间范围内的 tick（升序，空洞自动跳过）。</summary>
    public List<SampleTick> Range(DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
            return _slots.Where(t => t != null && t.TimeUtc >= fromUtc && t.TimeUtc < toUtc)
                         .OrderBy(t => t!.TimeUtc)
                         .Select(t => t!)
                         .ToList();
    }

    /// <summary>某分钟（UTC 整分）内的全部 tick。</summary>
    public List<SampleTick> Minute(DateTime minuteUtc) => Range(minuteUtc, minuteUtc.AddMinutes(1));

    /// <summary>最新采样时刻（实时 API 判断采样是否存活）；缓冲为空（启动后尚无 tick）返回 null。</summary>
    public DateTime? LastTickUtc
    {
        get
        {
            lock (_gate)
                return _slots.Where(t => t != null).Select(t => (DateTime?)t!.TimeUtc).Max();   // 空集合 → null
        }
    }

    /// <summary>该分钟是否已落库；未落库则标记（flush 前调用）。写库失败时调用 UnmarkFlushed 回退（下轮重试）。</summary>
    public bool MarkFlushedIfNew(DateTime minuteUtc)
    {
        lock (_gate) return _flushedMinutes.Add(minuteUtc);
    }

    /// <summary>回退分钟标记（落库失败后调用，配合下一轮 Flush 重试）。</summary>
    public void UnmarkFlushed(DateTime minuteUtc)
    {
        lock (_gate) _flushedMinutes.Remove(minuteUtc);
    }
}
