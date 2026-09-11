using DBPilot.Common;
using DBPilot.Core.Instances;
using DBPilot.SqlServer;

namespace DBPilot.UnitTests.InstanceTests;

public class PermissionCatalogTests
{
    [Fact]
    public void 全部权限齐备_无缺失()
    {
        var missing = PermissionCatalog.FindMissing(
        [
            "VIEW SERVER STATE", "VIEW ANY DATABASE", "VIEW ANY DEFINITION",
            "ALTER ANY EVENT SESSION", "CONNECT SQL"
        ]);

        Assert.Empty(missing);
    }

    [Fact]
    public void 缺失项_带影响说明与修复脚本()
    {
        var missing = PermissionCatalog.FindMissing(["CONNECT SQL"]);

        Assert.Equal(4, missing.Count);
        Assert.All(missing, m =>
        {
            Assert.False(m.Impact.IsNullOrWhiteSpace());
            Assert.Contains("GRANT", m.FixScript);
        });
        Assert.Contains(missing, m => m.Permission == "VIEW SERVER STATE");
    }

    [Fact]
    public void 权限名大小写不敏感()
    {
        var missing = PermissionCatalog.FindMissing(["view server state", "View Any Database", "VIEW ANY definition", "alter any event session"]);

        Assert.Empty(missing);
    }
}

public class ParseMajorVersionTests
{
    [Theory]
    [InlineData("10.0.1600.22", 10)]     // 2008
    [InlineData("10.50.2500.0", 105)]    // 2008 R2
    [InlineData("11.0.2100.60", 11)]     // 2012
    [InlineData("12.0.2000.8", 12)]      // 2014
    [InlineData("13.0.4001.0", 13)]      // 2016
    [InlineData("16.0.1000.6", 16)]      // 2022
    [InlineData("", 0)]
    [InlineData("junk", 0)]
    public void 常见版本号解析(string productVersion, int expected)
        => Assert.Equal(expected, SqlServerProvider.ParseMajorVersion(productVersion));
}

public class DeriveLogDirectoryTests
{
    [Fact]
    public void 错误日志全路径_剥末段文件名得目录()
        => Assert.Equal(@"E:\SQLDATA\MSSQL\LOG", SqlServerProvider.DeriveLogDirectory(@"E:\SQLDATA\MSSQL\LOG\ERRORLOG"));

    [Fact]
    public void 空值与无分隔符_返回null()
    {
        Assert.Null(SqlServerProvider.DeriveLogDirectory(null));
        Assert.Null(SqlServerProvider.DeriveLogDirectory("  "));
        Assert.Null(SqlServerProvider.DeriveLogDirectory("ERRORLOG"));   // 无 \ 分隔（根目录形态）
    }
}
