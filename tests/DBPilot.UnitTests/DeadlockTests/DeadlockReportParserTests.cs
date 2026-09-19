using System.Xml.Linq;
using DBPilot.Core.Deadlocks;

namespace DBPilot.UnitTests.DeadlockTests;

/// <summary>死锁 XML 解析：进程/资源/边/victim/时间转换/指纹稳定性。</summary>
public class DeadlockReportParserTests
{
    private static readonly TimeSpan CnOffset = TimeSpan.FromHours(8);   // 实例本地 = UTC+8

    /// <summary>典型互等死锁（两进程两 keylock，SHA 形态对齐真实 XE 报告）。</summary>
    private const string DeadlockXml = """
        <deadlock>
         <victim-list>
          <victimProcess id="processaaa1" />
         </victim-list>
         <process-list>
          <process id="processaaa1" taskpriority="0" logused="256" waitresource="KEY: 5:720575940 (61a3)" waittime="4312" ownerId="9" transactionname="user_transaction" lasttranstarted="2026-08-25T16:00:01.100" XDES="0x1" lockMode="X" schedulerid="1" kpid="0" status="suspended" spid="55" sbid="2" ecid="0" priority="0" trancount="2" lastbatchstarted="2026-08-25T16:00:02.200" lastbatchcompleted="2026-08-25T16:00:01.500" clientapp="Navicat" hostname="WS-A" loginname="sky" isolationlevel="read committed (2)" currentdatabase="5">
           <executionStack>
            <frame line="1" stmtstart="0" sqlhandle="0x03000" procname="adhoc">UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 1</frame>
            <frame line="2" sqlhandle="0x03001" procname="adhoc">UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 2</frame>
           </executionStack>
           <inputbuf>UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 1;
        UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 2;</inputbuf>
          </process>
          <process id="processbbb2" taskpriority="0" logused="128" waitresource="KEY: 5:720575941 (8f12)" waittime="4312" ownerId="10" transactionname="user_transaction" lasttranstarted="2026-08-25T16:00:01.300" XDES="0x2" lockMode="X" schedulerid="2" kpid="0" status="suspended" spid="58" sbid="2" ecid="0" priority="0" trancount="2" lastbatchstarted="2026-08-25T16:00:02.300" lastbatchcompleted="2026-08-25T16:00:01.600" clientapp="sqlcmd" hostname="WS-B" loginname="sky" isolationlevel="read committed (2)" currentdatabase="5">
           <executionStack>
            <frame line="1" stmthandle="0x03002" procname="adhoc">UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 2</frame>
           </executionStack>
           <inputbuf>UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 2;
        UPDATE dbo.Orders SET Amount = Amount + 1 WHERE Id = 1;</inputbuf>
          </process>
         </process-list>
         <resource-list>
          <keylock hobtid="720575940" databaseid="5" objectname="dbpilot_smoke.dbo.Orders" indexname="PK_Orders" id="lock1111" mode="X" associatedObjectId="720575940">
           <owner-list><owner id="processaaa1" mode="X" /></owner-list>
           <waiter-list><waiter id="processbbb2" mode="X" /></waiter-list>
          </keylock>
          <keylock hobtid="720575941" databaseid="5" objectname="dbpilot_smoke.dbo.Orders" indexname="PK_Orders" id="lock2222" mode="X" associatedObjectId="720575941">
           <owner-list><owner id="processbbb2" mode="X" /></owner-list>
           <waiter-list><waiter id="processaaa1" mode="X" /></waiter-list>
          </keylock>
         </resource-list>
        </deadlock>
        """;

    private static string EventXml(string ts, string graph)
    {
        var data = new XElement("data", new XAttribute("name", "xml_report"),
            new XElement("value", XElement.Parse(graph)));
        var evt = new XElement("event",
            new XAttribute("name", "xml_deadlock_report"),
            new XAttribute("package", "sqlserver"),
            new XAttribute("timestamp", ts),
            data);
        return evt.ToString(SaveOptions.DisableFormatting);
    }

    [Fact]
    public void ParseEvent_互等死锁_进程资源边victim()
    {
        var m = DeadlockReportParser.ParseEvent(
            EventXml("2026-08-25T08:00:05.500Z", DeadlockXml), CnOffset)!;

        Assert.NotNull(m);
        Assert.Equal(new DateTime(2026, 8, 25, 8, 0, 5, 500, DateTimeKind.Utc), m.EventTimeUtc);
        Assert.Equal(["processaaa1"], m.VictimProcessIds);

        Assert.Equal(2, m.Processes.Count);
        var victim = m.Processes.Single(p => p.Spid == 55);
        Assert.True(victim.IsVictim);
        Assert.Equal("sky", victim.LoginName);
        Assert.Equal("WS-A", victim.HostName);
        Assert.Equal("read committed (2)", victim.IsolationLevel);
        Assert.Equal("X", victim.LockMode);
        Assert.Equal(2, victim.Trancount);
        Assert.Equal(256, victim.LogUsed);
        Assert.Contains("UPDATE dbo.Orders", victim.InputBuf);
        Assert.Equal(2, victim.ExecutionStack.Count);
        Assert.All(victim.ExecutionStack, s => Assert.StartsWith("adhoc: ", s));
        Assert.False(m.Processes.Single(p => p.Spid == 58).IsVictim);

        // 本地时间（UTC+8）→ UTC：16:00:01.100 - 8h = 08:00:01.100
        Assert.Equal(new DateTime(2026, 8, 25, 8, 0, 1, 100, DateTimeKind.Utc), victim.LastTranStartedUtc);
        Assert.Equal(new DateTime(2026, 8, 25, 8, 0, 2, 200, DateTimeKind.Utc), victim.LastBatchStartedUtc);

        Assert.Equal(2, m.Resources.Count);
        var r1 = m.Resources.Single(r => r.Id == "lock1111");
        Assert.Equal("keylock", r1.ResourceType);
        Assert.Equal("dbpilot_smoke.dbo.Orders（PK_Orders）", r1.Display);
        Assert.Equal("X", r1.Mode);
        Assert.Single(r1.Owners);
        Assert.Equal(("processaaa1", "X"), (r1.Owners[0].ProcessId, r1.Owners[0].Mode));
        Assert.Equal(("processbbb2", "X"), (r1.Waiters[0].ProcessId, r1.Waiters[0].Mode));

        // 闭环：aaa1 等 lock2222、bbb2 等 lock1111
        var r2 = m.Resources.Single(r => r.Id == "lock2222");
        Assert.Equal("processaaa1", r2.Waiters[0].ProcessId);

        Assert.Equal(16, m.Fingerprint.Length);
        Assert.Contains("victim-list", m.GraphXml);
    }

    [Fact]
    public void 指纹_同构不同进程id_相等()
    {
        var a = DeadlockReportParser.Parse(DeadlockXml, DateTime.UtcNow, CnOffset);
        var b = DeadlockReportParser.Parse(DeadlockXml
            .Replace("processaaa1", "processzzz9").Replace("processbbb2", "processyyy8")
            .Replace("lock1111", "lock9999").Replace("lock2222", "lock8888"), DateTime.UtcNow, CnOffset);

        Assert.Equal(a.Fingerprint, b.Fingerprint);   // 结构相同 → 归并
    }

    [Fact]
    public void 指纹_不同对象或模式_不等()
    {
        var a = DeadlockReportParser.Parse(DeadlockXml, DateTime.UtcNow, CnOffset);
        var otherObject = DeadlockReportParser.Parse(
            DeadlockXml.Replace("dbo.Orders", "dbo.Users"), DateTime.UtcNow, CnOffset);
        var otherMode = DeadlockReportParser.Parse(
            DeadlockXml.Replace(@"waiter id=""processbbb2"" mode=""X""", @"waiter id=""processbbb2"" mode=""S"""),
            DateTime.UtcNow, CnOffset);

        Assert.NotEqual(a.Fingerprint, otherObject.Fingerprint);
        Assert.NotEqual(a.Fingerprint, otherMode.Fingerprint);
    }

    [Fact]
    public void ParseEvent_非死锁事件_null()
    {
        var evt = new XElement("event",
            new XAttribute("name", "error_reported"),
            new XAttribute("package", "sqlserver"),
            new XAttribute("timestamp", "2026-08-25T08:00:05Z")).ToString();

        Assert.Null(DeadlockReportParser.ParseEvent(evt, CnOffset));
    }

    [Fact]
    public void ParseTimestamp_双形态()
    {
        // ISO（event_file / ring_buffer 均为此格式）
        Assert.Equal(new DateTime(2026, 8, 25, 8, 0, 5, 500, DateTimeKind.Utc),
            DeadlockReportParser.ParseTimestamp("2026-08-25T08:00:05.500Z"));

        // 1601-01-01 起 64bit 微秒（旧形态兼容）：由期望时刻反推，避免手算错误
        var expected = new DateTime(2026, 8, 25, 8, 0, 5, DateTimeKind.Utc);
        Assert.Equal(expected, DeadlockReportParser.ParseTimestamp((expected.Ticks / 10).ToString()));
    }

    [Fact]
    public void 资源display_objectlock无索引_仅表名()
    {
        var xml = """
            <deadlock>
             <victim-list><victimProcess id="processa1" /></victim-list>
             <process-list>
              <process id="processa1" spid="60" lockMode="IX" />
              <process id="processb2" spid="61" lockMode="IX" />
             </process-list>
             <resource-list>
              <objectlock lockPartitionId="0" objid="123456" objectname="db1.dbo.T" databaseid="5" id="lockab" mode="IX" associatedObjectId="123456">
               <owner-list><owner id="processa1" mode="IX" /></owner-list>
               <waiter-list><waiter id="processb2" mode="IX" /></waiter-list>
              </objectlock>
             </resource-list>
            </deadlock>
            """;

        var m = DeadlockReportParser.Parse(xml, DateTime.UtcNow, TimeSpan.Zero);

        var r = Assert.Single(m.Resources);
        Assert.Equal("db1.dbo.T", r.Display);
        Assert.Equal(2, m.Processes.Count);   // 最小样本：缺属性不抛错
    }
}
