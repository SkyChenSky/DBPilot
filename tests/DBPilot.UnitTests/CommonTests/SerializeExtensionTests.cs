using DBPilot.Common;
using Newtonsoft.Json.Linq;

namespace DBPilot.UnitTests.CommonTests;

public class SerializeExtensionTests
{
    private class Sample
    {
        public string UserName { get; set; } = "dbpilot";
        public DateTime CreatedAt { get; set; } = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        public int? Age { get; set; }
    }

    [Fact]
    public void ToJson_统一设置_CamelCase与UTC()
    {
        var json = new Sample().ToJson();

        Assert.Contains("\"userName\":\"dbpilot\"", json);
        Assert.Contains("\"createdAt\":\"2026-08-20T12:00:00Z\"", json);
    }

    [Fact]
    public void ToJson_null对象返回空串()
        => Assert.Equal("", ((object?)null).ToJson());

    [Fact]
    public void ToJson_本地时间_转换为UTC()
    {
        var local = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Local);
        var json = new { t = local }.ToJson();

        // 本地时间序列化后必须转换为 UTC（带 Z 后缀），偏移为正时区日期应回退一天
        var expectedUtc = local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Assert.Contains($"\"t\":\"{expectedUtc}\"", json);
    }

    [Fact]
    public void ToJson_Null值属性默认保留()
    {
        var obj = JObject.Parse(new Sample().ToJson());

        Assert.True(obj.ContainsKey("age"));
        Assert.Equal(JTokenType.Null, obj["age"]!.Type);
    }

    [Fact]
    public void FromJson_还原对象()
    {
        var result = """{"userName":"admin","createdAt":"2026-01-02T03:04:05Z","age":null}""".FromJson<Sample>();

        Assert.NotNull(result);
        Assert.Equal("admin", result.UserName);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), result.CreatedAt);
        Assert.Null(result.Age);
    }

    [Fact]
    public void FromJson_空串返回default()
    {
        Assert.Null("".FromJson<Sample>());
        Assert.Null(((string?)null).FromJson<Sample>());
    }

    [Fact]
    public void ToList_正常解析()
    {
        var list = """[{"userName":"a"},{"userName":"b"}]""".ToList<Sample>();

        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].UserName);
    }

    [Fact]
    public void ToList_空串返回空列表()
    {
        Assert.Empty("".ToList<Sample>());
        Assert.Empty(((string?)null).ToList<Sample>());
    }

    [Fact]
    public void ToJson_ApiResponse_统一响应结构()
    {
        var obj = JObject.Parse(ApiResponse.Ok(new { id = 1 }).ToJson());

        Assert.Equal(0, (int)obj["code"]!);
        Assert.Equal("ok", (string?)obj["message"]);
        Assert.Equal(1, (int)obj["data"]!["id"]!);
    }

    [Fact]
    public void ToJson_PageList_分页结构()
    {
        var obj = JObject.Parse(PageList<string>.Create(["a", "b"], 2, 1, 20).ToJson());

        Assert.Equal(2, (long)obj["total"]!);
        Assert.Equal(1, (int)obj["pageIndex"]!);
        Assert.Equal(20, (int)obj["pageSize"]!);
        Assert.Equal(2, (obj["items"] as JArray)!.Count);
    }
}
