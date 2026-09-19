using System.ComponentModel;
using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class MiscExtensionTests
{
    private enum Status
    {
        [Description("启用")]
        Enabled,
        Disabled
    }

    [Fact]
    public void GetDescription_有特性返回描述_无特性返回枚举名()
    {
        Assert.Equal("启用", Status.Enabled.GetDescription());
        Assert.Equal("Disabled", Status.Disabled.GetDescription());
    }

    [Fact]
    public void GetDeepestException_取最内层()
    {
        var deepest = new ArgumentException("inner");
        Exception ex = new Exception("outer", new InvalidOperationException("middle", deepest));

        Assert.Same(deepest, ex.GetDeepestException());
    }

    [Fact]
    public void Enumerable与Object扩展()
    {
        List<int>? nullList = null;
        Assert.True(nullList.IsNullOrEmpty());
        Assert.True(new List<int>().IsNullOrEmpty());
        Assert.False(new[] { 1 }.IsNullOrEmpty());
        Assert.True(new[] { 1 }.IsNotNullOrEmpty());

        object? nullObj = null;
        Assert.True(nullObj.IsNull());
        Assert.False(new object().IsNull());
        Assert.Equal("", nullObj.ToStr());
        Assert.Equal("123", 123.ToStr());
    }

    [Theory]
    [InlineData(-1, "连接被监控实例失败")]
    [InlineData(2, "连接被监控实例失败")]
    [InlineData(53, "连接被监控实例失败")]
    [InlineData(10060, "连接被监控实例失败")]
    [InlineData(11001, "连接被监控实例失败")]
    [InlineData(-2, "查询超时，请稍后重试")]
    [InlineData(229, "权限不足，请检查被监控账号权限")]
    [InlineData(230, "权限不足，请检查被监控账号权限")]
    [InlineData(262, "权限不足，请检查被监控账号权限")]
    [InlineData(300, "权限不足，请检查被监控账号权限")]
    [InlineData(1205, "查询发生死锁/锁冲突，请重试")]
    [InlineData(500, "数据库查询失败")]
    [InlineData(null, "服务器内部错误")]
    public void FriendlyMessage_错误号分类(int? number, string expected)
    {
        Assert.Equal(expected, ExceptionExtension.Classify(number));
    }

    [Fact]
    public void FriendlyMessage_非Sql异常归服务器内部错误()
    {
        Exception ex = new Exception("outer", new InvalidOperationException("SELECT secret FROM x"));

        Assert.Equal("服务器内部错误", ex.FriendlyMessage());
    }
}
