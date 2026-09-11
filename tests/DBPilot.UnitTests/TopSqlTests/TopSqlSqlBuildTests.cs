using DBPilot.Core.TopSql;
using DBPilot.SqlServer;

namespace DBPilot.UnitTests.TopSqlTests;

/// <summary>BuildTopSqlSql 动态拼接（③ 自监控标记 / ④ 特征模式转义 / ⑤ 指纹校验与 NOT IN / ⑦ 兜底开关）。</summary>
public class TopSqlSqlBuildTests
{
    /// <summary>固定骨架自带一处 NOT IN（⑥ msdb/model 系统库排除），黑名单条件按出现次数区分。</summary>
    private static int NotInCount(string sql) => sql.Split("NOT IN").Length - 1;

    [Fact]
    public void 基础骨架_身份规则固定存在()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter());

        Assert.Contains("st.text IS NOT NULL", sql);                          // ① 文本不可解析
        Assert.Contains("st.text NOT LIKE '/* rds internal mark */%'", sql);  // ② RDS 官方标记
        Assert.Contains("st.text NOT LIKE '%/* dbpilot */%'", sql);           // ③ 平台自监控标记
        Assert.Equal(1, NotInCount(sql));                                     // 仅 ⑥，无黑名单条件
        Assert.DoesNotContain("(1 = 1 AND q.DbName = N'master')", sql);       // 开关关闭
    }

    [Fact]
    public void 特征模式_单引号转义防注入()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter
        {
            Patterns = ["%it's%", "  %sp_poll%  "],
        });

        Assert.Contains("st.text NOT LIKE '%it''s%'", sql);   // 单引号加倍
        Assert.Contains("st.text NOT LIKE '%sp_poll%'", sql); // 两端空白已 Trim
    }

    [Fact]
    public void 特征模式_空白与超量限量()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter
        {
            // 前两条空白各占 Take(100) 一个名额：p0~p97 拼接，p98 起丢弃
            Patterns = ["", "   ", .. Enumerable.Range(0, 150).Select(i => $"%p{i}%")],
        });

        Assert.DoesNotContain("NOT LIKE '%%'", sql);      // 空白模式不拼接（空 LIKE 会匹配全部）
        Assert.Contains("%p0%", sql);
        Assert.Contains("%p97%", sql);
        Assert.DoesNotContain("%p98%", sql);
        Assert.DoesNotContain("%p149%", sql);
    }

    [Fact]
    public void 指纹黑名单_合法hex拼NOT_IN()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter
        {
            Fingerprints = ["a3b6ca3ab05ec02f", "0123456789abcdef0123456789abcdef"],
        });

        Assert.Equal(2, NotInCount(sql));
        Assert.Contains("N'a3b6ca3ab05ec02f', N'0123456789abcdef0123456789abcdef'", sql);
    }

    [Theory]
    [InlineData("A3B6CA3AB05EC02F")]                       // 大写不匹配（Provider 侧指纹即小写）
    [InlineData("xyz")]                                    // 非 hex
    [InlineData("0123456789abcdef0123456789abcde")]        // 31 位：不落在 16 / 32~64
    [InlineData("0x1234567890abcdef")]                     // 0x 前缀
    public void 指纹黑名单_非法值忽略(string fp)
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter { Fingerprints = [fp, "   "] });

        Assert.Equal(1, NotInCount(sql));   // 只剩固定骨架的 ⑥
        Assert.DoesNotContain(fp.ToLowerInvariant() + "')", sql);
    }

    [Fact]
    public void 兜底开关_开时排除master()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter { ExcludeSystemDb = true });

        Assert.Contains("(1 = 0 OR q.DbName <> N'master')", sql);
    }

    [Fact]
    public void 兜底开关_关时保留master()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter { ExcludeSystemDb = false });

        Assert.Contains("(0 = 0 OR q.DbName <> N'master')", sql);
    }

    [Fact]
    public void 系统库排除_msdb与model固定存在()
    {
        var sql = SqlServerProvider.BuildTopSqlSql(new TopSqlFilter());

        Assert.Contains("q.DbName NOT IN (N'msdb', N'model')", sql);   // ⑥
        Assert.Contains("q.DbName IS NULL", sql);                      // ad hoc 行保留（NOT IN 三值逻辑）
    }
}
