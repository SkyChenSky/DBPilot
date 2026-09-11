using Chloe;
using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Blocking;
using DBPilot.Core.Collecting;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Serilog;

namespace DBPilot.Core.PerformanceInsight;

/// <summary>单实例一次采样的阻塞检测结果（并行阶段产出，落库阶段串行消费）。</summary>
public class BlockingDetection
{
    public int InstanceId { get; set; }
    public int ThresholdSec { get; set; }
    public DateTime SnapshotUtc { get; set; }
    public List<BlockingNode> QualifyingTrees { get; set; } = [];
}

/// <summary>
/// 会话采样服务（SessionSampleJob 每 10s 调用）：
/// 遍历启用实例 → GetActiveRequestsAsync（10s 采样）→ 环形缓冲；
/// 阻塞链最长等待 ≥ 实例阈值（blocking_threshold_sec，0 = 默认 10s）→ dbpilot_blocking_event 留痕（开/续/解除）。
/// 失败退避（Collect 节）按实例独立计数；平台库写入集中在并行阶段后串行执行（Chloe 上下文非线程安全）。
/// </summary>
public class SessionSamplingService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    SampleBufferRegistry registry, CollectStateStore states, CollectOptions options) : IDepend
{
    public const int DefaultBlockingThresholdSec = 10;

    /// <summary>主采集入口：遍历启用实例并行采样（写环形缓冲 + 阻塞链检测），检测结果集中后串行留痕落库。</summary>
    public async Task SampleAllAsync(CancellationToken ct = default)
    {
        var entities = await CollectRunner.LoadEnabledAsync(sp);
        if (entities is null) return;
        var db = sp.GetRequiredService<DbContext>();

        var detections = new ConcurrentBag<BlockingDetection>();
        var sampledInstanceIds = new ConcurrentBag<int>();

        await CollectRunner.ForEachEnabledAsync(entities, states, options, "采样", ct, async (e, token) =>
        {
            var state = states.Of(e.Id, "采样");
            var detection = await SampleOneAsync(e, token);
            state.OnSuccess();
            sampledInstanceIds.Add(e.Id);
            if (detection != null) detections.Add(detection);
        });

        // 平台库写（阻塞事件留痕）串行执行
        await CollectRunner.PersistAsync("阻塞事件留痕落库失败",
            () => PersistBlockingEventsAsync(db, [.. sampledInstanceIds], [.. detections]));
    }

    /// <summary>采样单实例：写环形缓冲；存在达到阈值的阻塞链时返回检测（含树快照）。</summary>
    private async Task<BlockingDetection?> SampleOneAsync(DbpilotInstance e, CancellationToken ct)
    {
        var cfg = InstanceConfigResolver.ToConfig(crypto, e);
        var rows = await provider.GetActiveRequestsAsync(cfg, ct);
        var now = DateTime.UtcNow;

        var buffer = registry.Of(e.Id);
        buffer.Add(SampleTickBuilder.Build(rows, now));
        buffer.RememberSqlTexts(rows);   // 指纹 → 语句文本（Load By SQL 显示用）

        // 阻塞链检测（复用阻塞树组装，含头阻塞者补查）
        var overview = BlockTreeBuilder.Build(rows, [], now);
        if (overview.ChainCount == 0) return null;

        var threshold = e.BlockingThresholdSec > 0 ? e.BlockingThresholdSec : DefaultBlockingThresholdSec;
        var qualifying = overview.Trees
            .Where(t => t.Children.Count > 0 && SubtreeMaxWaitMs(t) / 1000 >= threshold)
            .ToList();
        if (qualifying.Count == 0) return null;

        // 睡着头（事务开着锁拿着、会话空闲）不在请求行里，不补查则留痕缺 头会话/根阻塞SQL/数据库 三列。
        // 补查失败回落空列表 —— 树照常落库，留痕降级为无头信息
        var headIds = qualifying.Select(t => t.SessionId).Where(id => id > 0).Distinct().ToList();
        var heads = new List<HeadBlockerRow>();
        if (headIds.Count > 0)
        {
            try { heads = await provider.GetHeadBlockersAsync(cfg, headIds, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Debug(ex, "头阻塞者补查失败（留痕降级为无头信息）实例 {Id}", e.Id);
            }
        }
        if (heads.Count > 0)
            qualifying = BlockTreeBuilder.Build(rows, heads, now).Trees
                .Where(t => t.Children.Count > 0 && SubtreeMaxWaitMs(t) / 1000 >= threshold)
                .ToList();

        return new BlockingDetection
        {
            InstanceId = e.Id,
            ThresholdSec = threshold,
            SnapshotUtc = now,
            QualifyingTrees = qualifying,
        };
    }

    /// <summary>阻塞事件留痕落库：当前链 → 新开或续写（更新快照）；本 tick 采样成功但无链实例的未结事件 → 解除回填。</summary>
    private static async Task PersistBlockingEventsAsync(DbContext db, List<int> sampledInstanceIds, List<BlockingDetection> detections)
    {
        if (sampledInstanceIds.Count == 0) return;
        var sampled = sampledInstanceIds.ToHashSet();
        var byInstance = detections
            .Where(d => d.QualifyingTrees.Count > 0)
            .ToDictionary(d => d.InstanceId);

        var openEvents = await db.Query<DbpilotBlockingEvent>()
            .Where(x => !x.Resolved)
            .ToListAsync();

        // 同实例同头会话理论上仅一条未结事件，双进程并行或历史脏数据会产生重复：
        // 取最新一条续写，旧重复自动解除自愈
        var openByInstance = IndexOpenEvents(openEvents, out var staleDuplicates);
        var staleUtc = DateTime.UtcNow;
        foreach (var ev in staleDuplicates)
        {
            ev.Resolved = true;
            ev.EndTime = staleUtc;
            ev.UpdateTime = staleUtc;
            await db.UpdateAsync(ev);
        }

        foreach (var instanceId in sampled)
        {
            byInstance.TryGetValue(instanceId, out var detection);
            var current = detection?.QualifyingTrees ?? [];
            var currentHeads = current.Select(t => t.SessionId).ToHashSet();
            openByInstance.TryGetValue(instanceId, out var opens);

            // 续写 / 解除既有事件
            if (opens != null)
                foreach (var (head, ev) in opens)
                {
                    var tree = current.FirstOrDefault(t => t.SessionId == head);
                    if (tree != null)
                    {
                        ev.BlockedCount = CountBlocked(tree);
                        ev.MaxWaitSeconds = (int)(SubtreeMaxWaitMs(tree) / 1000);
                        // 头信息随续写刷新（首拍补查失败缺头信息、后续拍补到即自愈）
                        ev.HeadInfo = HeadInfo(tree);
                        ev.HeadDbName = tree.DbName;
                        ev.ChainTree = SerializeTree(tree);
                        ev.EndTime = null;
                        ev.Resolved = false;
                        ev.UpdateTime = DateTime.UtcNow;
                        await db.UpdateAsync(ev);
                    }
                    else
                    {
                        ev.Resolved = true;
                        ev.EndTime = DateTime.UtcNow;
                        ev.UpdateTime = DateTime.UtcNow;
                        await db.UpdateAsync(ev);
                    }
                }

            // 新开事件
            foreach (var tree in current.Where(t => opens == null || !opens.ContainsKey(t.SessionId)))
            {
                await db.InsertAsync(new DbpilotBlockingEvent
                {
                    InstanceId = instanceId,
                    HeadSessionId = tree.SessionId,
                    StartTime = DateTime.UtcNow,
                    BlockedCount = CountBlocked(tree),
                    MaxWaitSeconds = (int)(SubtreeMaxWaitMs(tree) / 1000),
                    HeadInfo = HeadInfo(tree),
                    HeadDbName = tree.DbName,
                    ChainTree = SerializeTree(tree),
                    Resolved = false,
                    CreateTime = DateTime.UtcNow,
                });
            }
        }
    }

    /// <summary>未结事件索引：实例 → 头会话 → 最新一条事件（纯函数，供单测）；同实例同头的旧重复收进 stale 供调用方解除。</summary>
    internal static Dictionary<int, Dictionary<int, DbpilotBlockingEvent>> IndexOpenEvents(
        List<DbpilotBlockingEvent> openEvents, out List<DbpilotBlockingEvent> stale)
    {
        stale = [];
        var byInstance = new Dictionary<int, Dictionary<int, DbpilotBlockingEvent>>();
        foreach (var g in openEvents.GroupBy(x => x.InstanceId))
        {
            var byHead = new Dictionary<int, DbpilotBlockingEvent>();
            foreach (var hg in g.GroupBy(x => x.HeadSessionId))
            {
                var newest = hg.OrderByDescending(x => x.Id).First();
                byHead[hg.Key] = newest;
                stale.AddRange(hg.Where(x => !ReferenceEquals(x, newest)));
            }
            byInstance[g.Key] = byHead;
        }
        return byInstance;
    }

    /// <summary>子树最长等待 ms（visited 防环，同 BlockTreeBuilder 逻辑）。</summary>
    internal static long SubtreeMaxWaitMs(BlockingNode n)
    {
        var visited = new HashSet<BlockingNode>();
        long Walk(BlockingNode node)
        {
            if (!visited.Add(node)) return 0;
            var max = node.WaitTimeMs;
            foreach (var c in node.Children)
                max = Math.Max(max, Walk(c));
            return max;
        }
        return Walk(n);
    }

    /// <summary>树内被阻塞会话数（不含根）。</summary>
    private static int CountBlocked(BlockingNode n)
    {
        var count = 0;
        void Walk(BlockingNode node)
        {
            count += node.Children.Count;
            foreach (var c in node.Children) Walk(c);
        }
        Walk(n);
        return count;
    }

    private static string HeadInfo(BlockingNode head) =>
        head.LoginName.FmtWho(head.HostName, head.ProgramName).Sub(200);

    private static string SerializeTree(BlockingNode tree) =>
        JsonConvert.SerializeObject(tree, SerializeExtension.CreateSettings());
}
