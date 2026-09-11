using DBPilot.Core.QueryPlan;
using DBPilot.Storage.Entities;

namespace DBPilot.UnitTests.QueryPlanTests;

/// <summary>计划快照变更检测（DetectPlanChanges 纯函数）测试。</summary>
public class DetectPlanChangesTests
{
    private const int InsId = 1;
    private static readonly DateTime Now = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

    private static QueryPlanRawRow Raw(string fp, string planHash, long exec = 10, long elapsedUs = 1_000_000,
        long workerUs = 500_000, long reads = 8000, string? handle = null, DateTime? creation = null) => new()
    {
        Fingerprint = fp,
        QueryPlanHash = planHash,
        DbName = "sales",
        PlanHandleHex = handle ?? "aabb",
        CreationTimeUtc = creation,
        ExecutionCount = exec,
        TotalElapsedUs = elapsedUs,
        TotalWorkerUs = workerUs,
        TotalLogicalReads = reads,
    };

    private static DbpilotQueryPlan Existing(long id, string fp, string planHash, long exec, long elapsedMs,
        long workerMs, long reads, DateTime lastSeen) => new()
    {
        Id = id,
        InstanceId = InsId,
        Fingerprint = fp,
        DbName = "sales",
        QueryPlanHash = planHash,
        FirstSeenUtc = lastSeen.AddDays(-1),
        LastSeenUtc = lastSeen,
        ExecutionCount = exec,
        TotalElapsedMs = elapsedMs,
        TotalWorkerMs = workerMs,
        TotalLogicalReads = reads,
    };

    [Fact]
    public void 指纹首见_只种子_不产事件()
    {
        var (inserts, updates, events) = QueryPlanCollectService.DetectPlanChanges(
            InsId, [], [Raw("f1", "p1"), Raw("f2", "p1")], Now);

        Assert.Equal(2, inserts.Count);
        Assert.Empty(updates);
        Assert.Empty(events);   // 部署首日全是种子
        Assert.All(inserts, x => Assert.Equal(Now, x.FirstSeenUtc));
    }

    [Fact]
    public void 已存在plan_hash_刷新累计与last_seen_保留first_seen()
    {
        var firstSeen = Now.AddDays(-3);
        var existing = new List<DbpilotQueryPlan>
        {
            new() { Id = 7, InstanceId = InsId, Fingerprint = "f1", QueryPlanHash = "p1",
                    FirstSeenUtc = firstSeen, LastSeenUtc = Now.AddHours(-1),
                    ExecutionCount = 5, TotalElapsedMs = 500, TotalWorkerMs = 300, TotalLogicalReads = 4000 },
        };

        var (inserts, updates, events) = QueryPlanCollectService.DetectPlanChanges(
            InsId, existing, [Raw("f1", "p1", exec: 20, elapsedUs: 4_000_000, reads: 20000)], Now);

        Assert.Empty(inserts);
        var u = Assert.Single(updates);
        Assert.Equal(7, u.Id);
        Assert.Equal(20, u.ExecutionCount);
        Assert.Equal(4000, u.TotalElapsedMs);       // µs → ms
        Assert.Equal(20000, u.TotalLogicalReads);
        Assert.Equal(Now, u.LastSeenUtc);
        Assert.Equal(firstSeen, u.FirstSeenUtc);    // 首见时间不动
        Assert.Empty(events);
    }

    [Fact]
    public void 计数器未前进_休眠计划不发UPDATE_last_seen保持旧值()
    {
        var existing = new List<DbpilotQueryPlan>
        {
            new() { Id = 7, InstanceId = InsId, Fingerprint = "f1", QueryPlanHash = "p1",
                    LastSeenUtc = Now.AddHours(-1),
                    ExecutionCount = 10, TotalElapsedMs = 1000, TotalWorkerMs = 500, TotalLogicalReads = 8000 },
        };

        // 累计值与库中完全一致（本窗口未执行，行仍在缓存）→ 差量语义跳过
        var (inserts, updates, events) = QueryPlanCollectService.DetectPlanChanges(
            InsId, existing, [Raw("f1", "p1", exec: 10, elapsedUs: 1_000_000, workerUs: 500_000, reads: 8000)], Now);

        Assert.Empty(inserts);
        Assert.Empty(updates);                       // 不发 UPDATE
        Assert.Equal(Now.AddHours(-1), existing[0].LastSeenUtc);   // last_seen 不被巡检刷新
        Assert.Empty(events);
    }

    [Fact]
    public void 计数未动但compile_time更早_仍刷新()
    {
        var existing = new List<DbpilotQueryPlan>
        {
            new() { Id = 7, InstanceId = InsId, Fingerprint = "f1", QueryPlanHash = "p1",
                    LastSeenUtc = Now.AddHours(-1),
                    ExecutionCount = 10, TotalElapsedMs = 1000, TotalWorkerMs = 500, TotalLogicalReads = 8000 },
        };
        var compile = Now.AddHours(-2);

        var (_, updates, _) = QueryPlanCollectService.DetectPlanChanges(
            InsId, existing, [Raw("f1", "p1", exec: 10, elapsedUs: 1_000_000, workerUs: 500_000, reads: 8000, creation: compile)], Now);

        var u = Assert.Single(updates);
        Assert.Equal(compile, u.CompileTimeUtc);     // 补齐更早的编译时间
        Assert.Equal(Now, u.LastSeenUtc);
    }

    [Fact]
    public void 同指纹新plan_hash_产事件_old取last_seen最大_含前后均值()
    {
        var older = Existing(1, "f1", "pa", exec: 10, elapsedMs: 1000, workerMs: 600, reads: 8000, lastSeen: Now.AddHours(-10));
        var newer = Existing(2, "f1", "pb", exec: 20, elapsedMs: 6000, workerMs: 3000, reads: 60000, lastSeen: Now.AddHours(-1));
        var compile = Now.AddMinutes(-5);

        var (inserts, updates, events) = QueryPlanCollectService.DetectPlanChanges(
            InsId, [older, newer], [Raw("f1", "pc", exec: 4, elapsedUs: 8_000_000, workerUs: 2_000_000, reads: 10000, creation: compile)], Now);

        var e = Assert.Single(events);
        Assert.Equal("pb", e.OldPlanHash);            // last_seen 最大的现有计划
        Assert.Equal("pc", e.NewPlanHash);
        Assert.Equal(compile, e.ChangedAtUtc);        // 变更时间 = 新计划 compile_time
        Assert.Equal(300, e.OldAvgElapsedMs);         // 6000/20
        Assert.Equal(2000, e.NewAvgElapsedMs);        // 8000ms/4
        Assert.Equal(150, e.OldAvgWorkerMs);          // 3000/20
        Assert.Equal(500, e.NewAvgWorkerMs);
        Assert.Equal(3000, e.OldAvgReads);            // 60000/20
        Assert.Equal(2500, e.NewAvgReads);
        Assert.Equal(20, e.OldExecCount);
        Assert.Equal(4, e.NewExecCount);
        Assert.Single(inserts);
        Assert.Empty(updates);
    }

    [Fact]
    public void 除零与缺老计划_均值为NULL()
    {
        // 老计划 exec=0 → old 均值 NULL；新计划 exec=0 → new 均值 NULL
        var old = Existing(1, "f1", "pa", exec: 0, elapsedMs: 0, workerMs: 0, reads: 0, lastSeen: Now.AddHours(-1));

        var (inserts, _, events) = QueryPlanCollectService.DetectPlanChanges(
            InsId, [old], [Raw("f1", "pb", exec: 0, elapsedUs: 0, workerUs: 0, reads: 0)], Now);

        var e = Assert.Single(events);
        Assert.Null(e.OldAvgElapsedMs);
        Assert.Null(e.NewAvgElapsedMs);
        Assert.Null(e.OldAvgWorkerMs);
        Assert.Null(e.NewAvgWorkerMs);
        Assert.Null(e.OldAvgReads);
        Assert.Null(e.NewAvgReads);
        Assert.Equal(Now, e.ChangedAtUtc);            // compile 缺失兜底采集时间
    }

    [Fact]
    public void 空输入_三集合皆空()
    {
        var (inserts, updates, events) = QueryPlanCollectService.DetectPlanChanges(InsId, [], [], Now);
        Assert.Empty(inserts);
        Assert.Empty(updates);
        Assert.Empty(events);
    }

    [Fact]
    public void 同指纹同plan_hash多handle_合并取首_计数求和()
    {
        var rows = new List<QueryPlanRawRow>
        {
            Raw("f1", "p1", exec: 3, elapsedUs: 3_000_000, handle: "h1", creation: Now.AddHours(-2)),
            Raw("f1", "p1", exec: 4, elapsedUs: 5_000_000, handle: "h2", creation: Now.AddHours(-1)),
        };

        var (inserts, _, _) = QueryPlanCollectService.DetectPlanChanges(InsId, [], rows, Now);

        var m = Assert.Single(inserts);
        Assert.Equal("h1", m.PlanHandleHex);          // handle 取首
        Assert.Equal(7, m.ExecutionCount);            // 计数求和
        Assert.Equal(8000, m.TotalElapsedMs);
        Assert.Equal(Now.AddHours(-2), m.CompileTimeUtc);   // creation 取 MIN
    }

    [Fact]
    public void 指纹首见_语句偏移随handle透传_供文本计划寻址()
    {
        var raw = Raw("f1", "p1", handle: "h1");
        raw.StatementStartOffset = 128;
        raw.StatementEndOffset = 512;

        var (inserts, _, _) = QueryPlanCollectService.DetectPlanChanges(InsId, [], [raw], Now);

        var m = Assert.Single(inserts);
        Assert.Equal(128, m.StatementStartOffset);
        Assert.Equal(512, m.StatementEndOffset);
    }
}

public class QueryPlanHandleRefTests
{
    [Fact]
    public void Key_handle小写归一_偏移拼接_同handle多语句不撞键()
    {
        var a = new QueryPlanHandleRef("AABB", 0, 100);
        var b = new QueryPlanHandleRef("aabb", 0, 100);

        Assert.Equal("aabb|0|100", a.Key);
        Assert.Equal(a.Key, b.Key);                       // 大小写归一：采集与抓取两侧来源一致
        Assert.NotEqual(a.Key, new QueryPlanHandleRef("aabb", 50, 100).Key);  // 偏移不同 = 不同语句
    }
}
