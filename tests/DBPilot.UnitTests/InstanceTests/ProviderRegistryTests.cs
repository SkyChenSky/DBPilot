using System.Reflection;
using DBPilot.Core.Blocking;
using DBPilot.Core.Deadlocks;
using DBPilot.Core.Indexes;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.Providers;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.SlowSql;
using DBPilot.Core.TopSql;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.UnitTests.InstanceTests;

/// <summary>
/// 引擎底座：ProviderRegistry 注册/路由 + RoutingDatabaseProvider 按 engine 委托 + 未注册引擎明确报错。
/// </summary>
public class ProviderRegistryTests
{
    /// <summary>测试用假引擎 Provider（仅实现 TestConnectionAsync 供路由断言）。</summary>
    [DbpilotEngine("fakeone")]
    private class FakeOneProvider : IDatabaseProvider
    {
        public Task<ConnectionTestResult> TestConnectionAsync(InstanceConfig cfg, CancellationToken ct = default)
            => Task.FromResult(new ConnectionTestResult { Ok = true, LatencyMs = 1 });

        public Task<InstanceMeta> ProbeAsync(InstanceConfig cfg, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string>> GetDatabasesAsync(InstanceConfig cfg, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<MissingIndexItem>> GetMissingIndexesAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<IndexUsageItem>> GetIndexUsageAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<FragTableInfo>> GetFragmentTablesAsync(InstanceConfig cfg, string dbName, int minPages, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<IndexFragmentItem>> GetIndexFragmentationAsync(InstanceConfig cfg, string dbName, int objectId, int minPages, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<TopSqlRawRow>> GetTopSqlRealtimeAsync(InstanceConfig cfg, string db, TopSqlFilter? filter = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ActiveRequestRow>> GetActiveRequestsAsync(InstanceConfig cfg, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<HeadBlockerRow>> GetHeadBlockersAsync(InstanceConfig cfg, List<int> headSessionIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<SessionLockRow>> GetSessionLocksAsync(InstanceConfig cfg, List<int> sessionIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DeadlockReadResult> ReadDeadlockEventsAsync(InstanceConfig cfg, DeadlockCursor? cursor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureSlowSqlCaptureAsync(InstanceConfig cfg, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlowSqlReadResult> PollSlowSqlAsync(InstanceConfig cfg, SlowSqlCursor? cursor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<QueryPlanRawRow>> GetQueryPlanStatsAsync(InstanceConfig cfg, TopSqlFilter? filter = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetQueryPlanXmlsAsync(InstanceConfig cfg, List<QueryPlanHandleRef> handles, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstanceMetricsSnapshot> GetInstanceMetricsAsync(InstanceConfig cfg, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<InstanceDiskRawRow>> GetInstanceDiskUsageAsync(InstanceConfig cfg, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [DbpilotEngine("faketwo")]
    private class FakeTwoProvider : FakeOneProvider;

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbpilotEngine<FakeOneProvider>();
        services.AddDbpilotEngine<FakeTwoProvider>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task 引擎注册_容器解析IDatabaseProvider为路由器_按engine委托调用()
    {
        using var sp = BuildProvider();

        var provider = sp.GetRequiredService<IDatabaseProvider>();
        Assert.IsType<RoutingDatabaseProvider>(provider);

        // 两个引擎各自路由到对应 Provider（不同实例），方法委托透传
        var one = await provider.TestConnectionAsync(new InstanceConfig { Engine = "fakeone" });
        var two = await provider.TestConnectionAsync(new InstanceConfig { Engine = "faketwo" });
        Assert.True(one.Ok);
        Assert.True(two.Ok);
    }

    [Fact]
    public async Task 引擎路由_大小写不敏感()
    {
        using var sp = BuildProvider();
        var provider = sp.GetRequiredService<IDatabaseProvider>();

        var result = await provider.TestConnectionAsync(new InstanceConfig { Engine = "FakeOne" });
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task 未注册引擎_抛Unsupported_报错含引擎名与已注册清单与接入指引()
    {
        using var sp = BuildProvider();
        var provider = sp.GetRequiredService<IDatabaseProvider>();

        var ex = await Assert.ThrowsAsync<DbpilotUnsupportedException>(
            () => provider.TestConnectionAsync(new InstanceConfig { Engine = "mysql" }));

        Assert.Equal("mysql", ex.Engine);
        Assert.Contains("未注册引擎「mysql」", ex.Message);
        Assert.Contains("fakeone", ex.Message);          // 已注册清单
        Assert.Contains("DBPilot.MySql", ex.Message);    // 接入指引（引用引擎包即自动注册）
    }

    [DbpilotEngine("dup")]
    private class FirstDupProvider : FakeOneProvider;

    [DbpilotEngine("dup")]
    private class SecondDupProvider : FakeOneProvider;

    [Fact]
    public void 同引擎重复注册_先注册者优先_不抛错()
    {
        // 显式注册 + 自动发现混用场景：注册表 GroupBy 取首项（幂等，不因重复键炸）
        var services = new ServiceCollection();
        services.AddDbpilotEngine<FirstDupProvider>();
        services.AddDbpilotEngine<SecondDupProvider>();
        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<ProviderRegistry>();
        Assert.IsType<FirstDupProvider>(registry.Resolve(sp, "dup"));
    }

    [Fact]
    public async Task 空引擎_同样抛Unsupported_不静默兜底()
    {
        using var sp = BuildProvider();
        var provider = sp.GetRequiredService<IDatabaseProvider>();

        // InstanceConfig 默认 sqlserver 不会被误判为空；显式清空模拟脏数据
        var ex = await Assert.ThrowsAsync<DbpilotUnsupportedException>(
            () => provider.TestConnectionAsync(new InstanceConfig { Engine = "" }));
        Assert.Contains("未注册引擎", ex.Message);
    }

    [Fact]
    public void Provider缺失引擎特性_注册期即抛_启动期暴露()
    {
        // AttributeUsage(Inherited=false)：派生类不继承标识 → 缺失即抛
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddDbpilotEngine<DerivedNoAttributeProvider>());
    }

    private class DerivedNoAttributeProvider : FakeOneProvider;

    // ---------------- Supports 能力矩阵 ----------------

    [DbpilotEngine("fakecap", UnsupportedFeatures = new[] { DbpilotFeatures.DeadlockEvents })]
    private class FakeCapProvider : FakeOneProvider;

    [Fact]
    public void Supports_特性声明的功能不支持_其余支持()
    {
        var services = new ServiceCollection();
        services.AddDbpilotEngine<FakeCapProvider>();
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<ProviderRegistry>();

        Assert.False(registry.Supports("fakecap", DbpilotFeatures.DeadlockEvents));
        Assert.True(registry.Supports("fakecap", DbpilotFeatures.QueryPlanSnapshot));
    }

    [Fact]
    public void Supports_空engine兜底sqlserver_未注册引擎fail_closed_大小写不敏感()
    {
        var registry = new ProviderRegistry(
        [
            new ProviderRegistration(DbpilotEngines.SqlServer, typeof(FakeOneProvider)),
            new ProviderRegistration(DbpilotEngines.MySql, typeof(FakeTwoProvider),
                new HashSet<string> { DbpilotFeatures.DeadlockEvents }),
        ]);

        // 空/白 = sqlserver（dbpilot_instance.engine 口径）
        Assert.True(registry.Supports(null, DbpilotFeatures.DeadlockEvents));
        Assert.True(registry.Supports(" ", DbpilotFeatures.DeadlockEvents));
        // 未注册引擎能力未知 = 不支持（fail-closed，与 Resolve 抛 Unsupported 同源）
        Assert.False(registry.Supports("postgresql", DbpilotFeatures.DeadlockEvents));
        // 匹配不区分大小写（与 Resolve 一致）
        Assert.False(registry.Supports("MySQL", DbpilotFeatures.DeadlockEvents));
        Assert.True(registry.Supports("SqlServer", DbpilotFeatures.DeadlockEvents));
    }

    [Fact]
    public void Supports_不支持时的拒答文案与Feature工厂逐字一致()
    {
        var registry = new ProviderRegistry(
            [new ProviderRegistration(DbpilotEngines.MySql, typeof(FakeTwoProvider),
                new HashSet<string> { DbpilotFeatures.DeadlockEvents })]);

        Assert.False(registry.Supports("mysql", DbpilotFeatures.DeadlockEvents));
        Assert.Equal("死锁事件读取不支持 mysql 实例（无对等数据源）",
            DbpilotUnsupportedException.Feature("mysql", DbpilotFeatures.DeadlockEvents).Message);
    }

    [Fact]
    public void 引擎包能力矩阵声明_MySql五项_SqlServer全支持()
    {
        var my = typeof(DBPilot.MySql.MySqlProvider).GetCustomAttribute<DbpilotEngineAttribute>()!;
        Assert.Equal(
        [
            DbpilotFeatures.DeadlockEvents, DbpilotFeatures.QueryPlanSnapshot,
            DbpilotFeatures.MissingIndex, DbpilotFeatures.Fragmentation, DbpilotFeatures.SlowSqlXeChannel,
        ], my.UnsupportedFeatures);

        var ss = typeof(DBPilot.SqlServer.SqlServerProvider).GetCustomAttribute<DbpilotEngineAttribute>()!;
        Assert.Empty(ss.UnsupportedFeatures);
    }
}
