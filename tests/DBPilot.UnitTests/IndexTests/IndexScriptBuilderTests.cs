using DBPilot.Core.Indexes;
using DBPilot.Core.Providers;

namespace DBPilot.UnitTests.IndexTests;

public class IndexScriptBuilderTests
{
    [Fact]
    public void 解析DMV列清单()
    {
        Assert.Equal(["A", "B"], IndexScriptBuilder.ParseColumns("[A], [B]"));
        Assert.Equal(["Amount"], IndexScriptBuilder.ParseColumns("[Amount]"));
        Assert.Empty(IndexScriptBuilder.ParseColumns(null));
        Assert.Empty(IndexScriptBuilder.ParseColumns(""));
    }

    [Fact]
    public void 短表名提取()
    {
        Assert.Equal("Orders", IndexScriptBuilder.ExtractShortTableName("[dbo].[Orders]"));
        Assert.Equal("T", IndexScriptBuilder.ExtractShortTableName("[T]"));
    }

    [Fact]
    public void 索引名_等值加不等值列()
        => Assert.Equal("IX_Orders_CustomerId_OrderDate",
            IndexScriptBuilder.BuildIndexName("[dbo].[Orders]", "[CustomerId]", "[OrderDate]"));

    [Fact]
    public void 索引名_超长截断加哈希()
    {
        var longCols = string.Join(", ", Enumerable.Range(0, 40).Select(i => $"[VeryLongColumnNameNumber{i}]"));
        var name = IndexScriptBuilder.BuildIndexName("[dbo].[Orders]", longCols, null);

        Assert.Equal(128, name.Length);
        Assert.StartsWith("IX_Orders_VeryLong", name);
        Assert.Contains('_', name[^9..]);   // 截断后带 8 位哈希后缀
    }

    [Fact]
    public void 创建脚本_含INCLUDE与ONLINE()
        => Assert.Equal(
            "CREATE INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] ([CustomerId]) INCLUDE ([Memo]) WITH (ONLINE = ON);",
            IndexScriptBuilder.BuildCreateIndexSql("[dbo].[Orders]", "[CustomerId]", null, "[Memo]", true));

    [Fact]
    public void 创建脚本_非Enterprise无ONLINE()
        => Assert.Equal(
            "CREATE INDEX [IX_Orders_A_B] ON [dbo].[Orders] ([A], [B]);",
            IndexScriptBuilder.BuildCreateIndexSql("[dbo].[Orders]", "[A]", "[B]", null, false));

    [Fact]
    public void 创建脚本_等值与不等值拼接键列()
        => Assert.Equal(
            "CREATE INDEX [IX_T_A_B] ON [dbo].[T] ([A], [B]);",
            IndexScriptBuilder.BuildCreateIndexSql("[dbo].[T]", "[A]", "[B]", null, false));

    [Theory]
    [InlineData("[A]", null, "A,B", true)]          // 建议 (A) ⊆ 现有 (A,B)
    [InlineData("[A]", "[B]", "A,B", true)]         // 建议 (A,B) = 现有 (A,B)
    [InlineData("[A]", "[B]", "A", false)]          // 建议 (A,B) ⊄ 现有 (A)：B 未覆盖，保留
    [InlineData("[A]", null, "B,A", false)]         // 顺序不同不算前缀
    [InlineData("[a]", null, "A,B", true)]          // 大小写不敏感
    [InlineData(null, null, "A,B", false)]          // 建议无键列（不可能出现，防御）
    [InlineData("[A]", null, null, false)]          // 现有无键列
    public void 已建索引覆盖判定(string? eq, string? ineq, string? existing, bool expected)
        => Assert.Equal(expected, IndexScriptBuilder.IsCoveredByExisting(eq, ineq, existing));

    [Fact]
    public void 未使用判定_读为零且写放大()
    {
        Assert.True(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 10001, UsedPageCount = 1001
        }));

        Assert.False(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 10001, UsedPageCount = 1001, UserSeeks = 1   // 有读
        }));
        Assert.False(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 10001, UsedPageCount = 1001, IsPrimaryKey = true   // 主键排除
        }));
        Assert.False(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 10001, UsedPageCount = 1001, IsUnique = true       // 唯一排除
        }));
        Assert.False(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 9999, UsedPageCount = 1001                          // 写不够
        }));
        Assert.False(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 10001, UsedPageCount = 999                          // 页数不够
        }));
        Assert.True(IndexScriptBuilder.IsUnused(new IndexUsageItem
        {
            UserUpdates = 10001                                               // 页数未知（MySQL 无每索引页数）：写放大已是强信号，不排除
        }));
    }

    [Fact]
    public void 禁用脚本()
        => Assert.Equal("ALTER INDEX [IX_Foo] ON [dbo].[T] DISABLE;",
            IndexScriptBuilder.BuildDisableSql(new IndexUsageItem
            {
                TableName = "[dbo].[T]", IndexName = "IX_Foo"
            }));

    [Fact]
    public void 禁用脚本_单入口按引擎选方言_MySql走INVISIBLE_大小写不敏感()
    {
        Assert.Equal("ALTER TABLE `db`.`t` ALTER INDEX `IX_Foo` INVISIBLE;",
            IndexScriptBuilder.BuildDisableSql(DbpilotEngines.MySql, "`db`.`t`", "IX_Foo"));
        Assert.Equal("ALTER TABLE `db`.`t` ALTER INDEX `IX_Foo` INVISIBLE;",
            IndexScriptBuilder.BuildDisableSql("MySQL", "`db`.`t`", "IX_Foo"));
        Assert.Equal("ALTER INDEX [IX_Foo] ON [dbo].[T] DISABLE;",
            IndexScriptBuilder.BuildDisableSql(DbpilotEngines.SqlServer, "[dbo].[T]", "IX_Foo"));
    }

    [Fact]
    public void 总览统计_各时间窗与占比()
    {
        var now = DateTime.UtcNow;
        var items = new List<MissingIndexItem>
        {
            new() { AvgUserImpact = 91, LastUserSeek = now.AddHours(-2) },    // 提升>80 + 近一天/周/月
            new() { AvgUserImpact = 50, LastUserSeek = now.AddDays(-3) },     // 近周/月
            new() { AvgUserImpact = 85, LastUserSeek = now.AddDays(-20) },    // 提升>80 + 近月
            new() { AvgUserImpact = 10, LastUserSeek = null },                // 无访问
        };

        var o = IndexDiagnoseService.BuildOverview(items);

        Assert.Equal(4, o.Total);
        Assert.Equal(2, o.HighImpact);
        Assert.Equal(1, o.LastDayCount);
        Assert.Equal(2, o.LastWeekCount);
        Assert.Equal(3, o.LastMonthCount);
        Assert.Equal(50, o.HighImpactPercent);
        Assert.Equal(25, o.LastDayPercent);
        Assert.Equal(50, o.LastWeekPercent);
        Assert.Equal(75, o.LastMonthPercent);
    }

    [Fact]
    public void 总览统计_空列表不除零()
    {
        var o = IndexDiagnoseService.BuildOverview([]);
        Assert.Equal(0, o.Total);
        Assert.Equal(0, o.HighImpactPercent);
    }

    // ---------------- 碎片 ----------------

    [Theory]
    [InlineData(0, FragAction.None)]
    [InlineData(9.9, FragAction.None)]
    [InlineData(10, FragAction.Reorganize)]      // 10% 含边界
    [InlineData(29.9, FragAction.Reorganize)]
    [InlineData(30, FragAction.Reorganize)]      // 30% 含边界（>30 才 REBUILD）
    [InlineData(30.1, FragAction.Rebuild)]
    [InlineData(99.9, FragAction.Rebuild)]
    public void 碎片处置建议_阈值边界(double frag, FragAction expected)
        => Assert.Equal(expected, IndexScriptBuilder.RecommendAction(frag));

    [Fact]
    public void 碎片脚本_REBUILD_Enterprise带ONLINE()
        => Assert.Equal(
            "ALTER INDEX [IX_Foo] ON [dbo].[T] REBUILD WITH (ONLINE = ON);",
            IndexScriptBuilder.BuildFragScript("[dbo].[T]", "IX_Foo", FragAction.Rebuild, true, 1));

    [Fact]
    public void 碎片脚本_REBUILD_非Enterprise无ONLINE()
        => Assert.Equal(
            "ALTER INDEX [IX_Foo] ON [dbo].[T] REBUILD;",
            IndexScriptBuilder.BuildFragScript("[dbo].[T]", "IX_Foo", FragAction.Rebuild, false, 1));

    [Fact]
    public void 碎片脚本_REORGANIZE_无ONLINE选项()
        => Assert.Equal(
            "ALTER INDEX [IX_Foo] ON [dbo].[T] REORGANIZE;",
            IndexScriptBuilder.BuildFragScript("[dbo].[T]", "IX_Foo", FragAction.Reorganize, true, 1));

    [Fact]
    public void 碎片脚本_分区索引只处理目标分区()
    {
        Assert.Equal(
            "ALTER INDEX [IX_Foo] ON [dbo].[T] REBUILD PARTITION = 3 WITH (ONLINE = ON);",
            IndexScriptBuilder.BuildFragScript("[dbo].[T]", "IX_Foo", FragAction.Rebuild, true, 3));
        Assert.Equal(
            "ALTER INDEX [IX_Foo] ON [dbo].[T] REORGANIZE PARTITION = 2;",
            IndexScriptBuilder.BuildFragScript("[dbo].[T]", "IX_Foo", FragAction.Reorganize, false, 2));
    }

    [Fact]
    public void 碎片脚本_None返回空串()
        => Assert.Equal("", IndexScriptBuilder.BuildFragScript("[dbo].[T]", "IX_Foo", FragAction.None, true, 1));
}
