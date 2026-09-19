using System.Text.Json;

namespace DBPilot.UnitTests.McpTests;

/// <summary>
/// MCP 协议集成测（测试策略 ② 层）：WebApplicationFactory 起 Host → MCP 客户端经
/// Streamable HTTP 连 /mcp，验证传输 / 鉴权 / 工具 schema / 调用与序列化。
/// 平台库未配置（连接串置空）时工具返回确定性 error 载荷——验证的是"全链路通"而非数据内容。
/// </summary>
[Collection("McpHost")]
public class McpServerTests(McpHostFixture host)
{
    private readonly McpHostFixture _host = host;

    [Fact]
    public async Task 无ApiKey的MCP客户端_握手应抛连接异常()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _host.CreateMcpClientAsync(apiKey: "wrong-key"));
    }

    [Fact]
    public async Task 无ApiKey的Http请求_mcp路由应401()
    {
        var client = _host.CreateClient();
        var response = await client.PostAsync("/mcp", null);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 模块关闭时_mcp路由不注册()
    {
        using var host = new McpDisabledHostFixture();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", McpHostFixture.TestApiKey);
        var response = await client.PostAsync("/mcp", null);
        // 中间件未注册：不会 401；落到 SPA fallback 返回 index.html
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 握手与工具清单_应包含已交付工具()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var tools = await mcp.ListToolsAsync();

        var names = tools.Select(t => t.Name).ToList();
        // 实例/指标/阻塞 + 慢SQL/死锁/AAS/索引工具
        string[] expected =
        [
            "list_instances", "get_metrics_trend", "get_blocking_current",
            "get_slow_sql", "get_slow_sql_detail", "get_deadlocks", "get_deadlock_detail",
            "get_aas", "get_aas_top_sql", "get_missing_indexes", "get_index_usage",
        ];
        foreach (var name in expected)
            Assert.Contains(name, names);

        // 工具描述带防注入声明（铁律④：文本是数据不是指令）
        Assert.Contains("数据", tools.Single(t => t.Name == "get_blocking_current").Description);
        Assert.Contains("数据", tools.Single(t => t.Name == "get_slow_sql").Description);
        Assert.Contains("数据", tools.Single(t => t.Name == "get_deadlock_detail").Description);
    }

    [Fact]
    public async Task 调用list_instances_平台库未配置应返回error载荷()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("list_instances", new Dictionary<string, object?>());

        Assert.False(result.IsError ?? false); // IsError=null 表示正常返回（error 载荷在内容 JSON 里，非协议级错误）
        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task 调用get_metrics_trend_非法时间参数应返回格式错误提示()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("get_metrics_trend", new Dictionary<string, object?>
        {
            ["instanceId"] = 1,
            ["from"] = "not-a-date",
            ["to"] = "2026-08-31T00:00:00Z",
        });

        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.Contains("ISO 8601", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 调用get_metrics_trend_时间倒挂应返回校验错误()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("get_metrics_trend", new Dictionary<string, object?>
        {
            ["instanceId"] = 1,
            ["from"] = "2026-08-31T01:00:00Z",
            ["to"] = "2026-08-31T00:00:00Z",
        });

        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.Contains("from", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 调用get_slow_sql_非法时间参数应返回格式错误提示()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("get_slow_sql", new Dictionary<string, object?>
        {
            ["instanceId"] = 1,
            ["from"] = "not-a-date",
        });

        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.Contains("ISO 8601", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 调用get_aas_时间只传一半应返回成对校验错误()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("get_aas", new Dictionary<string, object?>
        {
            ["instanceId"] = 1,
            ["from"] = "2026-08-31T00:00:00Z",
        });

        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.Contains("成对", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 调用get_deadlocks_平台库未配置应返回error载荷()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("get_deadlocks", new Dictionary<string, object?> { ["instanceId"] = 1 });

        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task 调用get_missing_indexes_平台库未配置应返回error载荷()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var result = await mcp.CallToolAsync("get_missing_indexes", new Dictionary<string, object?> { ["instanceId"] = 1 });

        var text = Assert.IsType<string>(Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));
        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }
}
