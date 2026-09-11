using DBPilot.Common;

namespace DBPilot.Core.Blocking;

/// <summary>
/// 阻塞链树组装（平台侧纯函数）：活动请求平铺行 → 阻塞树森林。
/// 头阻塞者"睡着拿锁"（无活动请求）由补查行合成节点；blocking_session_id = -2/-3
/// 合成系统节点（孤儿分布式事务 / 延迟恢复，无对应会话）。
/// </summary>
public static class BlockTreeBuilder
{
    public const int OrphanDtcSessionId = -2;
    public const int DeferredRecoverySessionId = -3;

    /// <summary>
    /// 组装：只返回真正的阻塞链（被阻塞的行才进树；与阻塞无关的普通请求不出现）。
    /// involvedSessionIds = 各行 blocking_session_id 去重（含 -2/-3），用于补查与统计。
    /// </summary>
    public static BlockingOverview Build(List<ActiveRequestRow> requests, List<HeadBlockerRow> headBlockers, DateTime snapshotUtc)
    {
        // 被阻塞行（blocking_session_id <> 0 即存在阻塞来源；0 = 无阻塞）
        var blocked = requests.Where(r => r.BlockingSessionId != 0).ToList();

        var overview = new BlockingOverview { SnapshotTimeUtc = snapshotUtc };
        if (blocked.Count == 0) return overview;

        var heads = headBlockers.ToDictionary(h => h.SessionId);
        // 节点池：活动请求行优先；睡着头 / 系统节点不在请求里，按需合成
        var nodes = new Dictionary<int, BlockingNode>();

        BlockingNode NodeOf(int sessionId)
        {
            if (nodes.TryGetValue(sessionId, out var n)) return n;

            var row = requests.FirstOrDefault(r => r.SessionId == sessionId);
            if (row != null)
            {
                n = FromRequest(row);
            }
            else if (heads.TryGetValue(sessionId, out var h))
            {
                // 睡着拿锁：无活动请求但被指认为阻塞头；等待时长 = 最早活动事务至今
                n = FromSleepingHead(h, snapshotUtc);
            }
            else if (sessionId is OrphanDtcSessionId or DeferredRecoverySessionId)
            {
                // 系统节点：-2 孤儿分布式事务 / -3 延迟恢复（无会话实体）
                n = new BlockingNode
                {
                    SessionId = sessionId,
                    IsSystem = true,
                    Status = sessionId == OrphanDtcSessionId ? "孤儿分布式事务" : "延迟恢复",
                    OpenTranCount = 1,
                };
            }
            else
            {
                // 会话已消失（刚好结束）——保留占位节点防止链断裂
                n = new BlockingNode { SessionId = sessionId, Status = "已结束" };
            }
            nodes[sessionId] = n;
            return n;
        }

        // 挂链：被阻塞节点挂到阻塞头下（挂的过程中自然合成链头节点）。
        // 自阻塞（并行查询 blocking_session_id = 自身）跳过，防自环。
        foreach (var r in blocked)
        {
            var child = NodeOf(r.SessionId);
            var parent = NodeOf(r.BlockingSessionId);
            if (!ReferenceEquals(child, parent))
                parent.Children.Add(child);
        }

        // 根 = 没有被任何行指认为阻塞来源的头节点（头自己也是被阻塞行 → 属于更大链的中段，不算根）。
        // 极端瞬时的互相阻塞环无根 → 该分量整体不出树（DMV 瞬态，下轮轮询自愈）。
        var blockedIds = blocked.Select(r => r.SessionId).ToHashSet();
        var roots = nodes.Values
            .Where(n => !blockedIds.Contains(n.SessionId))
            .OrderByDescending(n => MaxWaitMs(n, []))
            .ToList();

        SetDepth(roots, 0);

        overview.Trees = roots;
        overview.ChainCount = roots.Count;
        overview.InvolvedSessions = nodes.Count;
        overview.MaxWaitSeconds = nodes.Values.Max(n => n.WaitTimeMs) / 1000;
        return overview;
    }

    private static BlockingNode FromRequest(ActiveRequestRow r) => new()
    {
        SessionId = r.SessionId,
        LoginName = r.LoginName,
        HostName = r.HostName,
        ProgramName = r.ProgramName,
        DbName = r.DbName,
        Status = r.Status,
        Command = r.Command,
        WaitType = r.WaitType,
        WaitTimeMs = r.WaitTimeMs,
        WaitResource = r.WaitResource,
        TotalElapsedMs = r.TotalElapsedMs,
        OpenTranCount = r.OpenTranCount,
        SqlText = r.SqlText.IsNullOrEmpty() ? null : r.SqlText.Trim().Sub(2000),
        BatchSqlText = r.BatchSqlText.IsNullOrEmpty() ? null : r.BatchSqlText.Trim().Sub(4000),
    };

    private static BlockingNode FromSleepingHead(HeadBlockerRow h, DateTime snapshotUtc) => new()
    {
        SessionId = h.SessionId,
        IsSleepingHead = true,
        LoginName = h.LoginName,
        HostName = h.HostName,
        ProgramName = h.ProgramName,
        DbName = h.DbName,
        OpenTranCount = h.OpenTranCount,
        Status = "sleeping",
        // 睡着头等待时长用事务持有时长表达：最早活动事务至今
        WaitTimeMs = h.TransactionBeginUtc is { } t
            ? Math.Max(0, (long)(snapshotUtc - t).TotalMilliseconds)
            : 0,
        WaitType = "SLEEPING（持有事务锁）",
        SqlText = h.LastSqlText.IsNullOrEmpty() ? null : h.LastSqlText.Trim().Sub(2000),
        // 睡着头的 LastSqlText 本身就是 input_buffer / 最近批全文 → 同作批全文
        BatchSqlText = h.LastSqlText.IsNullOrEmpty() ? null : h.LastSqlText.Trim().Sub(4000),
    };

    /// <summary>
    /// 阻塞原因（锁资源）挂接：树内真实会话（非系统节点）按 sessionId 匹配 dm_tran_locks 行；
    /// 每会话最多 10 条，WAIT（正在等的锁）在前、GRANT（已持有）在后 —— 根阻塞者看 GRANT、
    /// 被阻塞方看 WAIT，两者指向同一资源即"阻塞原因"。
    /// </summary>
    public static void AttachLocks(BlockingOverview overview, List<SessionLockRow> rows)
    {
        var bySession = rows
            .GroupBy(r => r.SessionId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(r => r.LockStatus == "WAIT" ? 0 : 1)
                .ThenBy(r => r.ResourceType)
                .ThenBy(r => r.LockMode)
                .Take(10)
                .Select(r => new BlockingLockInfo
                {
                    ResourceType = r.ResourceType,
                    DbName = r.DbName,
                    ObjectName = r.ObjectName,
                    LockMode = r.LockMode,
                    LockStatus = r.LockStatus,
                })
                .ToList());

        Walk(overview.Trees);
        return;

        void Walk(List<BlockingNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (!n.IsSystem && bySession.TryGetValue(n.SessionId, out var locks))
                    n.Locks = locks;
                Walk(n.Children);
            }
        }
    }

    /// <summary>子树最长等待（visited 防御环数据）。</summary>
    private static long MaxWaitMs(BlockingNode n, HashSet<BlockingNode> visited)
    {
        if (!visited.Add(n)) return 0;
        var max = n.WaitTimeMs;
        foreach (var c in n.Children)
            max = Math.Max(max, MaxWaitMs(c, visited));
        return max;
    }

    private static void SetDepth(List<BlockingNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            n.Depth = depth;
            SetDepth(n.Children, depth + 1);
        }
    }
}
