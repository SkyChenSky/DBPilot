using Chloe;
using DBPilot.Common;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.Core.Blocking;

/// <summary>
/// 实时阻塞（按需查询不走定时 Job）：dm_exec_requests 直查 → 平台侧组装阻塞树；
/// 存在阻塞时自动补查头阻塞者"睡着拿锁"特征。
/// 事件留痕（≥ 阈值落库 + 解除回填）由采样 Job 承担；
/// 历史查询（本服务分页 + 详情）读留痕表。
/// </summary>
public class BlockingService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider) : IDepend
{
    /// <summary>历史阻塞分页：时间范围 + 状态 + 库筛选，按开始时间倒序。</summary>
    public async Task<PageList<BlockingEventListItem>?> GetHistoryPageAsync(
        int instanceId, DateTime? from, DateTime? to, bool? resolved, string? dbName, int page, int limit)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var cond = ExpressionBuilder.Init<DbpilotBlockingEvent>()
            .And(x => x.InstanceId == instanceId);
        if (from != null)
            cond = cond.And(x => x.StartTime >= from);
        if (to != null)
            cond = cond.And(x => x.StartTime < to);
        if (resolved != null)
            cond = cond.And(x => x.Resolved == resolved);
        if (!string.IsNullOrEmpty(dbName))
            cond = cond.And(x => x.HeadDbName == dbName);

        var pageList = await db.Query<DbpilotBlockingEvent>()
            .Where(cond)
            .OrderByDesc(x => x.StartTime)
            .PageListAsync(page, limit);

        return PageList<BlockingEventListItem>.Create(
            pageList.Items.Select(ToListItem),
            pageList.Total, pageList.PageIndex, pageList.PageSize);
    }

    /// <summary>阻塞数量统计（历史 tab 顶部卡片）：近一天 / 近一周 / 近两周（按开始时间口径）。</summary>
    public async Task<BlockingStats?> GetStatsAsync(int instanceId)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        var now = DateTime.UtcNow;
        return new BlockingStats
        {
            DayCount = await CountSinceAsync(db, instanceId, now.AddDays(-1)),
            WeekCount = await CountSinceAsync(db, instanceId, now.AddDays(-7)),
            TwoWeekCount = await CountSinceAsync(db, instanceId, now.AddDays(-14)),
        };
    }

    private static async Task<int> CountSinceAsync(DbContext db, int instanceId, DateTime since) =>
        await db.Query<DbpilotBlockingEvent>()
            .Where(x => x.InstanceId == instanceId && x.StartTime >= since)
            .CountAsync();

    /// <summary>
    /// 阻塞趋势（历史 tab 趋势图）：查询跨度内按时间桶聚合 事件次数 + 最长等待秒累计，
    /// 桶粒度自适应（≤2h→5min / ≤24h→15min / ≤72h→1h / 其余→2h，点数约 24~90）。
    /// </summary>
    public async Task<BlockingTrend?> GetTrendAsync(int instanceId, DateTime from, DateTime to)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return null;

        // 轻量投影（chain_tree 是 NVARCHAR(MAX)，不能整实体载入）
        var rows = await db.Query<DbpilotBlockingEvent>()
            .Where(x => x.InstanceId == instanceId && x.StartTime >= from && x.StartTime < to)
            .Select(x => new TrendRow { StartTime = x.StartTime, MaxWaitSeconds = x.MaxWaitSeconds })
            .ToListAsync();

        return Bucketize(rows.Select(r => (r.StartTime, r.MaxWaitSeconds)), from, to);
    }

    /// <summary>趋势聚合行（查询投影用）。</summary>
    public sealed class TrendRow
    {
        public DateTime StartTime { get; set; }
        public int MaxWaitSeconds { get; set; }
    }

    /// <summary>时间桶聚合（internal 供单测）：桶起点对齐 step、空桶补零、覆盖 [from, to)。</summary>
    internal static BlockingTrend Bucketize(IEnumerable<(DateTime StartTime, int MaxWaitSeconds)> rows, DateTime from, DateTime to)
    {
        var spanMinutes = Math.Max(1, (int)Math.Ceiling((to - from).TotalMinutes));
        var step = spanMinutes <= 120 ? 5
            : spanMinutes <= 1440 ? 15
            : spanMinutes <= 4320 ? 60
            : 120;

        var start = from.AddTicks(-(from.Ticks % (TimeSpan.TicksPerMinute * step)));
        var buckets = new List<BlockingTrendItem>();
        var index = new Dictionary<DateTime, BlockingTrendItem>();
        for (var t = start; t < to; t = t.AddMinutes(step))
        {
            var item = new BlockingTrendItem { BucketStartUtc = t };
            buckets.Add(item);
            index[t] = item;
        }

        foreach (var (startTime, maxWaitSeconds) in rows)
        {
            var key = startTime.AddTicks(-(startTime.Ticks % (TimeSpan.TicksPerMinute * step)));
            if (index.TryGetValue(key, out var item))
            {
                item.Count++;
                item.TotalWaitSeconds += maxWaitSeconds;
            }
        }

        return new BlockingTrend { StepMinutes = step, Items = buckets };
    }

    /// <summary>历史阻塞事件详情：chain_tree JSON → 阻塞树快照。</summary>
    public async Task<ServiceResult<BlockingEventDetail>> GetEventDetailAsync(int eventId)
    {
        var db = sp.GetService<DbContext>();
        if (db is null)
            return ServiceResult<BlockingEventDetail>.Failed(InstanceConfigResolver.DbNotConfiguredFor("查询历史阻塞"));

        var e = await db.Query<DbpilotBlockingEvent>().FirstOrDefaultAsync(x => x.Id == eventId);
        if (e is null)
            return ServiceResult<BlockingEventDetail>.Failed($"阻塞事件 {eventId} 不存在");

        // chain_tree 与序列化同源的 CamelCase 规则；脏数据容错为 null（详情弹窗仅展示元信息）
        BlockingNode? tree = null;
        try { tree = e.ChainTree.FromJson<BlockingNode>(); }
        catch { /* JsonReaderException 等脏数据 */ }

        return ServiceResult<BlockingEventDetail>.Succeeded(new BlockingEventDetail
        {
            Id = e.Id,
            InstanceId = e.InstanceId,
            HeadSessionId = e.HeadSessionId,
            StartTimeUtc = DateTime.SpecifyKind(e.StartTime, DateTimeKind.Utc),
            EndTimeUtc = e.EndTime is null ? null : DateTime.SpecifyKind(e.EndTime.Value, DateTimeKind.Utc),
            BlockedCount = e.BlockedCount,
            MaxWaitSeconds = e.MaxWaitSeconds,
            HeadInfo = e.HeadInfo,
            HeadDbName = e.HeadDbName,
            HeadSql = HeadSqlOf(e),
            Resolved = e.Resolved,
            Tree = tree,
        });
    }

    private static BlockingEventListItem ToListItem(DbpilotBlockingEvent e) => new()
    {
        Id = e.Id,
        HeadSessionId = e.HeadSessionId,
        StartTimeUtc = DateTime.SpecifyKind(e.StartTime, DateTimeKind.Utc),
        EndTimeUtc = e.EndTime is null ? null : DateTime.SpecifyKind(e.EndTime.Value, DateTimeKind.Utc),
        BlockedCount = e.BlockedCount,
        MaxWaitSeconds = e.MaxWaitSeconds,
        HeadInfo = e.HeadInfo,
        HeadDbName = e.HeadDbName,
        HeadSql = HeadSqlOf(e),
        Resolved = e.Resolved,
    };

    /// <summary>头阻塞者 SQL 预览：chain_tree 根节点语句级文本，缺省兜底整批文本（归一空白，最长 200；脏数据容错 null）。</summary>
    private static string? HeadSqlOf(DbpilotBlockingEvent e)
    {
        try
        {
            var tree = e.ChainTree.FromJson<BlockingNode>();
            var sql = tree?.SqlText ?? tree?.BatchSqlText;
            if (string.IsNullOrWhiteSpace(sql)) return null;
            return sql.NormalizeWhitespace().Sub(200);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ServiceResult<BlockingOverview>> GetRealtimeAsync(int instanceId)
    {
        var (err, entity) = await InstanceConfigResolver.LoadAsync(sp, instanceId);
        if (err != null) return ServiceResult<BlockingOverview>.Failed(err);

        List<ActiveRequestRow> requests;
        try
        {
            var cfg = InstanceConfigResolver.ToConfig(crypto, entity!);
            requests = await provider.GetActiveRequestsAsync(cfg);

            // 存在阻塞 → 定点补查头阻塞者（去重、剔除 -2/-3 系统值与自身）
            var headIds = requests.Where(r => r.BlockingSessionId != 0)
                .Select(r => r.BlockingSessionId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            List<HeadBlockerRow> heads = [];
            if (headIds.Count > 0)
                heads = await provider.GetHeadBlockersAsync(cfg, headIds);

            var overview = BlockTreeBuilder.Build(requests, heads, DateTime.UtcNow);

            // 阻塞原因（锁资源）：查涉及会话的对象级锁（等待 + 持有）挂到节点上
            var lockSessionIds = new HashSet<int>();
            CollectSessions(overview.Trees, lockSessionIds);
            if (lockSessionIds.Count > 0)
                BlockTreeBuilder.AttachLocks(overview, await provider.GetSessionLocksAsync(cfg, lockSessionIds.ToList()));

            return ServiceResult<BlockingOverview>.Succeeded(overview);
        }
        catch (Exception ex)
        {
            return ServiceResult<BlockingOverview>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>树内真实会话 id（剔除 -2/-3 系统节点）。</summary>
    private static void CollectSessions(List<BlockingNode> nodes, HashSet<int> ids)
    {
        foreach (var n in nodes)
        {
            if (!n.IsSystem && n.SessionId > 0)
                ids.Add(n.SessionId);
            CollectSessions(n.Children, ids);
        }
    }
}
