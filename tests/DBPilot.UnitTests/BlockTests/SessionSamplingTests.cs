using DBPilot.Core.PerformanceInsight;
using DBPilot.Storage.Entities;

namespace DBPilot.UnitTests.BlockTests;

/// <summary>会话采样留痕：未结事件索引的重复头容错（双进程并行同拍各插一套的自愈口径）。</summary>
public class SessionSamplingTests
{
    private static DbpilotBlockingEvent Ev(int id, int instanceId, int headSessionId) => new()
    {
        Id = id,
        InstanceId = instanceId,
        HeadSessionId = headSessionId,
        Resolved = false,
    };

    [Fact]
    public void 未结事件索引_同实例同头重复_取最新一条()
    {
        var open = new List<DbpilotBlockingEvent>
        {
            Ev(37, 3, 97),
            Ev(38, 3, 97),   // 双进程同拍重复
            Ev(39, 3, 103),
            Ev(41, 5, 200),
        };

        var index = SessionSamplingService.IndexOpenEvents(open, out var stale);

        Assert.Equal(38, index[3][97].Id);          // 最新
        Assert.Equal(39, index[3][103].Id);
        Assert.Equal(41, index[5][200].Id);
        Assert.Equal([37], stale.Select(x => x.Id).ToList());   // 旧重复进 stale 供解除
    }

    [Fact]
    public void 未结事件索引_无重复_stale为空()
    {
        var open = new List<DbpilotBlockingEvent> { Ev(1, 3, 10), Ev(2, 3, 20) };

        var index = SessionSamplingService.IndexOpenEvents(open, out var stale);

        Assert.Equal(2, index[3].Count);
        Assert.Empty(stale);
    }
}
