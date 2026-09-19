using DBPilot.Common;
using Newtonsoft.Json;

namespace DBPilot.UnitTests.CommonTests;

public class ServiceResultTests
{
    [Fact]
    public void Succeeded_工厂()
    {
        var result = ServiceResult.Succeeded("done", new { id = 1 });

        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.Message);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void Failed_工厂()
    {
        var result = ServiceResult.Failed("参数错误");

        Assert.False(result.IsSuccess);
        Assert.Equal("参数错误", result.Message);
    }

    [Fact]
    public void ErrorException_携带异常但不外泄到JSON()
    {
        var inner = new InvalidOperationException("boom");
        var result = ServiceResult.ErrorException(inner);

        Assert.False(result.IsSuccess);
        Assert.Same(inner, result.Exception);
        Assert.Equal("boom", result.Message);

        var json = result.ToJson();
        Assert.Contains("\"message\":\"boom\"", json);                          // Message 正常输出
        Assert.DoesNotContain("exception", json.ToLowerInvariant());           // Exception 不参与序列化
    }

    [Fact]
    public void 泛型版_数据往返()
    {
        var ok = ServiceResult<int>.Succeeded(42);
        Assert.True(ok.IsSuccess);
        Assert.Equal(42, ok.Data);

        var fail = ServiceResult<int>.Failed("错误");
        Assert.False(fail.IsSuccess);
        Assert.Equal(0, fail.Data);   // int 默认值
    }
}
