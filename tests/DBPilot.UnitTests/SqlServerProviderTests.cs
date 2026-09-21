using DBPilot.SqlServer;

namespace DBPilot.UnitTests;

/// <summary>SQL Server Provider 纯函数/常量 SQL 测试。</summary>
public class SqlServerProviderTests
{
    [Fact]
    public void ActiveRequestsSql_骨架_含标记与自监控排除()
    {
        var sql = SqlServerProvider.ActiveRequestsSql;

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("sys.dm_exec_requests", sql);
        Assert.Contains("sys.dm_exec_sql_text(r.sql_handle)", sql);   // 语句文本按 offset 截取
        Assert.Contains("r.session_id >= 50", sql);                   // 系统会话排除
        Assert.Contains("r.session_id <> @@SPID", sql);               // 排除自身连接
        Assert.Contains("ISNULL(s.program_name, '') <> 'DBPilot'", sql);   // 平台自监控连接不进采样（对齐 XE client_app_name 口径）
    }

    [Fact]
    public void BuildPlanXmlSql_双通道_XML优先文本兜底()
    {
        var sql = SqlServerProvider.BuildPlanXmlSql(["aa", "BB"]);

        Assert.Contains("/* dbpilot */", sql);
        Assert.Contains("sys.dm_exec_query_plan(qs.plan_handle) qp", sql);            // 第一通道：整批 XML
        Assert.Contains("sys.dm_exec_text_query_plan(qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset) tp", sql); // 第二通道：语句级文本（XML 为 NULL 时兜底，无嵌套上限）
        Assert.Contains("ISNULL(CONVERT(nvarchar(max), qp.query_plan)", sql);         // XML 优先
        Assert.Contains("CONVERT(nvarchar(max), tp.query_plan))", sql);               // 文本兜底
        Assert.Contains("qs.statement_start_offset                  AS StartOffset", sql); // 键含语句偏移（同 handle 多语句不撞键）
        Assert.Contains("UPPER(N'aa'), UPPER(N'BB')", sql);                           // handle 批量 IN
    }

    [Fact]
    public void BuildHeadBlockerLastSql_三分支按版本与列存在性()
    {
        // ≥12（2014+）：dm_exec_input_buffer（更准的输入缓冲）
        var (apply, col) = SqlServerProvider.BuildHeadBlockerLastSql(12, hasSessionsMostRecentSqlHandle: false);
        Assert.Contains("sys.dm_exec_input_buffer", apply);
        Assert.Equal("ib.event_info", col);

        // <12 且 sessions 有列（自建 2008/2008R2/2012）：most_recent_sql_handle
        (apply, col) = SqlServerProvider.BuildHeadBlockerLastSql(11, hasSessionsMostRecentSqlHandle: true);
        Assert.Contains("s.most_recent_sql_handle", apply);
        Assert.Equal("ib.text", col);

        // <12 且 sessions 无列（RDS 安全改造剥列，实测 RDS 2012 SP4）：connections 兜底
        (apply, col) = SqlServerProvider.BuildHeadBlockerLastSql(11, hasSessionsMostRecentSqlHandle: false);
        Assert.Contains("sys.dm_exec_connections c", apply);
        Assert.Contains("c.most_recent_sql_handle", apply);
        Assert.Equal("ib.text", col);
    }
}
