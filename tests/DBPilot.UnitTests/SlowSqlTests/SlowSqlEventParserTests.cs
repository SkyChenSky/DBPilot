using DBPilot.Core.SlowSql;
using Xunit;

namespace DBPilot.UnitTests.SlowSqlTests;

/// <summary>fn_xe 文件形态的事件样本（value 为转义文本）。</summary>
internal static class SlowSqlSamples
{
    public const string RpcEvent = """
        <event name="rpc_completed" package="sqlserver" timestamp="2026-08-26T03:00:05.120Z">
          <data name="statement"><value>EXEC dbo.SomeProc @p=N'x'</value></data>
          <data name="duration"><value type="uint64">2500000</value></data>
          <data name="cpu_time"><value type="uint32">300000</value></data>
          <data name="logical_reads"><value type="uint64">1234</value></data>
          <data name="physical_reads"><value type="uint64">10</value></data>
          <data name="writes"><value type="uint64">2</value></data>
          <data name="row_count"><value type="uint64">100</value></data>
          <action name="database_name" package="sqlserver"><value>dbpilot_smoke</value></action>
          <action name="client_app_name" package="sqlserver"><value>Navicat</value></action>
          <action name="client_hostname" package="sqlserver"><value>LAPTOP-X</value></action>
          <action name="username" package="sqlserver"><value>sky</value></action>
          <action name="session_id" package="sqlserver"><value type="int32">74</value></action>
        </event>
        """;

    public const string BatchEvent = """
        <event name="sql_batch_completed" package="sqlserver" timestamp="2026-08-26T03:00:10.000Z">
          <data name="batch_text"><value>USE dbpilot_smoke;
        WAITFOR DELAY '00:00:03';
        SELECT * FROM dbo.SmokeDlA;</value></data>
          <data name="duration"><value type="uint64">3000</value></data>
          <data name="cpu_time"><value type="uint32">100</value></data>
          <action name="database_name" package="sqlserver"><value>dbpilot_smoke</value></action>
          <action name="username" package="sqlserver"><value>sky</value></action>
          <action name="session_id" package="sqlserver"><value type="int32">75</value></action>
        </event>
        """;
}

public class SlowSqlEventParserTests
{
    [Fact]
    public void RpcEvent_解析全部字段()
    {
        var r = SlowSqlEventParser.ParseEvent(SlowSqlSamples.RpcEvent, durationIsMicroseconds: true)!;

        Assert.NotNull(r);
        Assert.Equal(new DateTime(2026, 8, 26, 3, 0, 5, 120, DateTimeKind.Utc), r.EventTimeUtc);
        Assert.Equal("dbpilot_smoke", r.DbName);
        Assert.Equal("sky", r.LoginName);
        Assert.Equal("LAPTOP-X", r.HostName);
        Assert.Equal("Navicat", r.AppName);
        Assert.Equal(74, r.SessionId);
        Assert.Equal(1, r.SqlType);
        Assert.Equal(2500, r.DurationMs);          // 2_500_000 µs → 2500ms
        Assert.Equal(300, r.CpuMs);
        Assert.Equal(1234, r.LogicalReads);
        Assert.Equal(10, r.PhysicalReads);
        Assert.Equal(2, r.Writes);
        Assert.Equal(100, r.RowCount);
        Assert.Contains("SomeProc", r.SqlText);
        Assert.Equal(16, r.Fingerprint.Length);
    }

    [Fact]
    public void BatchEvent_取整批文本_类型2()
    {
        var r = SlowSqlEventParser.ParseEvent(SlowSqlSamples.BatchEvent, durationIsMicroseconds: false)!;

        Assert.NotNull(r);
        Assert.Equal(2, r.SqlType);
        Assert.Equal(3000, r.DurationMs);          // ms 口径不除
        Assert.Contains("WAITFOR DELAY", r.SqlText);
    }

    [Fact]
    public void 非慢SQL事件_返回Null()
    {
        var r = SlowSqlEventParser.ParseEvent(
            """<event name="xml_deadlock_report" package="sqlserver" timestamp="2026-08-26T03:00:00.000Z"><data name="x"><value>1</value></data></event>""",
            durationIsMicroseconds: true);

        Assert.Null(r);
    }

    [Fact]
    public void 文本缺失_返回Null()
    {
        var r = SlowSqlEventParser.ParseEvent(
            """<event name="sql_batch_completed" package="sqlserver" timestamp="2026-08-26T03:00:00.000Z"><data name="duration"><value>2000</value></data></event>""",
            durationIsMicroseconds: false);

        Assert.Null(r);
    }

    [Fact]
    public void 超长文本_64KB截断()
    {
        var xml = $"""<event name="sql_batch_completed" package="sqlserver" timestamp="2026-08-26T03:00:00.000Z"><data name="batch_text"><value>{new string('a', 70_000)}</value></data><data name="duration"><value>2000</value></data></event>""";

        var r = SlowSqlEventParser.ParseEvent(xml, durationIsMicroseconds: false)!;

        Assert.Equal(64 * 1024, r.SqlText.Length);
    }
}

public class SlowSqlFingerprintTests
{
    [Theory]
    [InlineData(
        "SELECT * FROM dbo.T WHERE Name = N'alice' AND Age > 20",
        "select * from dbo.t where name = ? and age > ?")]           // 参数替换 + 小写
    [InlineData(
        "SELECT  *   FROM dbo.T\nWHERE Name='bob'",
        "select * from dbo.t where name=?")]                          // 空白折叠
    [InlineData(
        "update t set v = 0x1F where id = 5",
        "update t set v = ? where id = ?")]                           // 十六进制
    public void Normalize_规则(string sql, string expected)
    {
        Assert.Equal(expected, SlowSqlEventParser.Normalize(sql));
    }

    [Fact]
    public void 标识符中的数字不被替换()
    {
        var n = SlowSqlEventParser.Normalize("SELECT col1, a2_b, @p3 FROM t2026");
        Assert.Equal("select col1, a2_b, @p3 from t2026", n);
    }

    [Fact]
    public void 同模板不同参数_指纹相同()
    {
        var a = SlowSqlEventParser.FingerprintOf("SELECT * FROM dbo.Orders WHERE Id = 123 AND Name = N'alice'");
        var b = SlowSqlEventParser.FingerprintOf("select * from dbo.orders where id = 999 and name = N'bob'");

        Assert.Equal(a, b);
    }

    [Fact]
    public void 不同模板_指纹不同()
    {
        var a = SlowSqlEventParser.FingerprintOf("SELECT * FROM dbo.Orders WHERE Id = 1");
        var b = SlowSqlEventParser.FingerprintOf("DELETE FROM dbo.Orders WHERE Id = 1");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 字符串内嵌转义引号_整体替换()
    {
        var a = SlowSqlEventParser.FingerprintOf("SELECT * FROM t WHERE s = 'o''clock'");
        var b = SlowSqlEventParser.FingerprintOf("SELECT * FROM t WHERE s = 'x'");

        Assert.Equal(a, b);
    }
}
