using DBPilot.Core.Collecting;
using DBPilot.Core.Deadlocks;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.SlowSql;

namespace DBPilot.UnitTests.CollectingTests;

public class CollectStateStoreTests
{
    [Fact]
    public void 同实例不同采集器_失败计数互不干扰()
    {
        var states = new CollectStateStore();

        // 低频采集器连续失败 4 次（未达阈值 5，不退避）
        var deadlock = states.Of(1, "死锁采集");
        for (var i = 0; i < 4; i++)
            Assert.False(deadlock.OnFailure(5, 30));

        // 高频采集器成功 —— 只清自己（旧口径会顺带清掉死锁的计数 → 永远静默）
        states.Of(1, "采样").OnSuccess();

        var deadlockAgain = states.Of(1, "死锁采集");
        Assert.Same(deadlock, deadlockAgain);
        Assert.False(deadlockAgain.InBackoff);
        Assert.True(deadlockAgain.OnFailure(5, 30));   // 第 5 次达阈值，进入退避
    }

    [Fact]
    public void 同采集器同实例_拿到同一状态对象()
    {
        var states = new CollectStateStore();

        Assert.Same(states.Of(1, "采样"), states.Of(1, "采样"));
        Assert.NotSame(states.Of(1, "采样"), states.Of(2, "采样"));
    }
}

public class XeCursorRollbackTests
{
    [Fact]
    public void 死锁游标_Set后可回退到旧值()
    {
        var cursors = new XeCursorStore();
        DeadlockCursor? old = null;
        cursors.SetDeadlock(1, old);

        // 并行段推进
        var advanced = new DeadlockCursor { FileName = "system_health.xel", Offset = 1024, TimestampUtc = DateTime.UtcNow };
        cursors.SetDeadlock(1, advanced);
        Assert.Same(advanced, cursors.DeadlockOf(1));

        // 落库失败回退旧游标
        cursors.SetDeadlock(1, old);
        Assert.Null(cursors.DeadlockOf(1));
    }

    [Fact]
    public void 慢SQL游标_Set后可回退到旧值()
    {
        var cursors = new XeCursorStore();
        var old = new SlowSqlCursor { FileName = "DBPilot_SlowSql.xel", Offset = 100 };
        cursors.SetSlowSql(1, old);

        var advanced = new SlowSqlCursor { FileName = "DBPilot_SlowSql.xel", Offset = 5000 };
        cursors.SetSlowSql(1, advanced);

        cursors.SetSlowSql(1, old);
        Assert.Same(old, cursors.SlowSqlOf(1));
        Assert.Equal(100, cursors.SlowSqlOf(1)!.Offset);
    }
}

public class SampleBufferFlushTests
{
    [Fact]
    public void 分钟标记_Mark后Unmark可重新Mark()
    {
        var buffer = new InstanceSampleBuffer();
        var minute = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(buffer.MarkFlushedIfNew(minute));     // 首次标记成功
        Assert.False(buffer.MarkFlushedIfNew(minute));    // 已标记

        buffer.UnmarkFlushed(minute);                     // 落库失败回退
        Assert.True(buffer.MarkFlushedIfNew(minute));     // 下轮重试可重新标记
    }
}
