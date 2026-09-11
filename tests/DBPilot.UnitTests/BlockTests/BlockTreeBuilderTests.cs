using DBPilot.Core.Blocking;

namespace DBPilot.UnitTests.BlockTests;

/// <summary>阻塞链树组装：普通链 / 睡着头 / -2/-3 系统节点 / 自阻塞防环 / 空场景。</summary>
public class BlockTreeBuilderTests
{
    private static readonly DateTime Snap = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ActiveRequestRow Row(int sessionId, int blockedBy, long waitMs = 1000, string? waitType = "LCK_M_S") => new()
    {
        SessionId = sessionId,
        BlockingSessionId = blockedBy,
        Status = "suspended",
        WaitType = waitType,
        WaitTimeMs = waitMs,
        TotalElapsedMs = waitMs + 100,
    };

    [Fact]
    public void 无阻塞_返回空总览()
    {
        var o = BlockTreeBuilder.Build([Row(60, 0), Row(61, 0)], [], Snap);

        Assert.Equal(0, o.ChainCount);
        Assert.Empty(o.Trees);
        Assert.Equal(0, o.InvolvedSessions);
    }

    [Fact]
    public void 两级链_头为活动请求()
    {
        // 52 阻塞 53，53 阻塞 54：头 52 有活动请求（自己也在跑）
        var o = BlockTreeBuilder.Build([Row(53, 52), Row(54, 53), Row(52, 0, waitMs: 5000)], [], Snap);

        var root = Assert.Single(o.Trees);
        Assert.Equal(52, root.SessionId);
        Assert.Equal(0, root.Depth);
        var mid = Assert.Single(root.Children);
        Assert.Equal(53, mid.SessionId);
        Assert.Equal(1, mid.Depth);
        Assert.Equal(54, Assert.Single(mid.Children).SessionId);
        Assert.Equal(2, Assert.Single(mid.Children).Depth);
        Assert.Equal(3, o.InvolvedSessions);
        Assert.Equal(1, o.ChainCount);
    }

    [Fact]
    public void 睡着头_由补查行合成_等待等于事务持有时长()
    {
        var tranBegin = Snap.AddSeconds(-30);
        var o = BlockTreeBuilder.Build(
            [Row(60, 55)],
            [new HeadBlockerRow { SessionId = 55, LoginName = "app", OpenTranCount = 1, TransactionBeginUtc = tranBegin, LastSqlText = "UPDATE t ...", DbName = "dbpilot" }],
            Snap);

        var root = Assert.Single(o.Trees);
        Assert.Equal(55, root.SessionId);
        Assert.True(root.IsSleepingHead);
        Assert.False(root.IsSystem);
        Assert.Equal("app", root.LoginName);
        Assert.Equal("dbpilot", root.DbName);   // 补查带库 → 睡着头"数据库"列不缺（留痕/实时页同源）
        Assert.Equal(30_000, root.WaitTimeMs);
        Assert.Contains("UPDATE", root.SqlText);
        Assert.Equal(60, Assert.Single(root.Children).SessionId);
    }

    [Fact]
    public void 系统节点_负2孤儿事务_负3延迟恢复()
    {
        var o = BlockTreeBuilder.Build([Row(60, -2), Row(61, -3)], [], Snap);

        Assert.Equal(2, o.ChainCount);
        Assert.Equal(-2, o.Trees.Single(t => t.SessionId == -2).SessionId);
        Assert.Equal(-3, o.Trees.Single(t => t.SessionId == -3).SessionId);
        Assert.All(o.Trees, t => Assert.True(t.IsSystem));
        Assert.Equal(4, o.InvolvedSessions);
    }

    [Fact]
    public void 自阻塞_不挂自环不出树()
    {
        // 并行查询瞬态：blocking_session_id = 自身
        var o = BlockTreeBuilder.Build([Row(60, 60, waitMs: 2000)], [], Snap);

        Assert.Equal(0, o.ChainCount);
        Assert.Empty(o.Trees);
    }

    [Fact]
    public void 会话已消失_占位节点防链断裂()
    {
        // 头会话刚好结束：不在请求也不在补查，保留占位
        var o = BlockTreeBuilder.Build([Row(60, 58)], [], Snap);

        var root = Assert.Single(o.Trees);
        Assert.Equal(58, root.SessionId);
        Assert.Equal("已结束", root.Status);
    }

    [Fact]
    public void 多棵独立树_按子树最长等待降序()
    {
        // 链 A：头 50 → 51（等待 8000）；链 B：头 70 → 71（等待 3000）
        var o = BlockTreeBuilder.Build([Row(51, 50, 8000), Row(50, 0, 100), Row(71, 70, 3000), Row(70, 0, 100)], [], Snap);

        Assert.Equal(2, o.ChainCount);
        Assert.Equal(2, o.Trees.Count);
        Assert.Equal(50, o.Trees[0].SessionId);   // 子树最长等待 8000 > 3000
        Assert.Equal(70, o.Trees[1].SessionId);
        Assert.Equal(8, o.MaxWaitSeconds);
    }

    [Fact]
    public void 头自身也是被阻塞行_归属更大链不独立成根()
    {
        // 55 睡着头阻塞 60，但 55 又被 52 阻塞（睡着头带活动请求的混合场景）：
        // 55 在请求行里 → 作为中段节点
        var o = BlockTreeBuilder.Build([Row(55, 52), Row(60, 55), Row(52, 0)], [], Snap);

        var root = Assert.Single(o.Trees);
        Assert.Equal(52, root.SessionId);
        Assert.Equal(55, Assert.Single(root.Children).SessionId);
        Assert.Equal(60, Assert.Single(Assert.Single(root.Children).Children).SessionId);
        Assert.False(root.Children.Single().IsSleepingHead);   // 有活动请求，不是睡着头
    }

    // ---------- AttachLocks（阻塞原因：锁资源挂接） ----------

    private static SessionLockRow Lock(int sessionId, string mode, string status, string type = "KEY", string? obj = "dbo.T") => new()
    {
        SessionId = sessionId,
        ResourceType = type,
        DbName = "db1",
        EntityId = 72057594043564032,
        LockMode = mode,
        LockStatus = status,
        ObjectName = obj,
    };

    [Fact]
    public void 锁挂接_等待在前持有在后()
    {
        var o = BlockTreeBuilder.Build([Row(53, 52), Row(52, 0)], [], Snap);

        BlockTreeBuilder.AttachLocks(o,
        [
            Lock(52, "X", "GRANT"),
            Lock(52, "IX", "GRANT", type: "OBJECT"),
            Lock(53, "X", "WAIT"),
        ]);

        var root = o.Trees.Single();
        var victim = root.Children.Single();

        // 根（阻塞者）：只有 GRANT，2 条
        Assert.Equal(2, root.Locks.Count);
        Assert.All(root.Locks, l => Assert.Equal("GRANT", l.LockStatus));
        // 被阻塞方：WAIT 在前
        Assert.Single(victim.Locks);
        Assert.Equal("WAIT", victim.Locks[0].LockStatus);
        Assert.Equal("X", victim.Locks[0].LockMode);
        Assert.Equal("dbo.T", victim.Locks[0].ObjectName);
    }

    [Fact]
    public void 锁挂接_每会话最多10条_无锁会话不挂()
    {
        var o = BlockTreeBuilder.Build([Row(53, 52), Row(52, 0)], [], Snap);

        var rows = Enumerable.Range(0, 15).Select(i => Lock(52, "X", "GRANT", obj: $"dbo.T{i}")).ToList();
        BlockTreeBuilder.AttachLocks(o, rows);

        Assert.Equal(10, o.Trees.Single().Locks.Count);          // 截断到 10
        Assert.Empty(o.Trees.Single().Children.Single().Locks);  // 53 无锁行 → 空列表
    }

    [Fact]
    public void 锁挂接_系统节点不匹配锁()
    {
        // -2 孤儿分布式事务：IsSystem，即便锁行 session = -2 也不挂
        var o = BlockTreeBuilder.Build([Row(60, -2)], [], Snap);
        BlockTreeBuilder.AttachLocks(o, [Lock(-2, "X", "GRANT")]);

        Assert.Empty(o.Trees.Single().Locks);
    }
}
