using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace DBPilot.UnitTests.McpTests;

/// <summary>
/// MCP 测试宿主（测试策略 ② 层）：WebApplicationFactory 起真实组合链的 Host（不占端口）。
/// 覆盖配置走 UseSetting——最小托管下它对 Program 组装期（AddDbpilotMcp 读 builder.Configuration）即生效，
/// ConfigureAppConfiguration 的内存源要到 Build 后才并入（组装期读不到，实测坑）。
/// 平台库连接串置空（各服务走"平台库未配置"分支，确定性可断言）；Quartz Job 照常注册但无平台库上下文即空转跳过。
/// </summary>
public class McpHostFixture : WebApplicationFactory<Program>
{
    public const string TestApiKey = "unit-test-api-key";

    /// <summary>模块开关（派生类 McpDisabledHostFixture 置 false 验证关闭语义）。</summary>
    protected virtual bool McpEnabled => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DBPilot:ConnectionString", "");
        // PlatformEngine 无默认值（必须显式指定）：测试宿主固定 sqlserver（引擎模块经自动发现注册）
        builder.UseSetting("DBPilot:PlatformEngine", "sqlserver");
        // ApiKey 非空即开启（无独立 Enabled 开关）：关闭语义 = 显式置空（须覆盖宿主 appsettings 里可能存在的真实 Key）
        builder.UseSetting("DBPilot:Mcp:ApiKey", McpEnabled ? TestApiKey : "");
    }

    /// <summary>MCP 客户端（Streamable HTTP 连测试服务器 /mcp，带测试 ApiKey；调用方负责 Dispose）。</summary>
    public async Task<McpClient> CreateMcpClientAsync(string? apiKey = TestApiKey)
    {
        var httpClient = CreateClient();
        httpClient.BaseAddress ??= new Uri("http://localhost"); // TestServer 处理器不校验主机，仅需绝对地址
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp, // 测试不走 SSE 回退（避免失败时的慢速重连）
            AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey ?? "" },
        }, httpClient, ownsHttpClient: false);

        return await McpClient.CreateAsync(transport);
    }
}

/// <summary>模块开关关闭的宿主（验证 Enabled=false → /mcp 不注册）。</summary>
public class McpDisabledHostFixture : McpHostFixture
{
    protected override bool McpEnabled => false;
}

/// <summary>
/// MCP 测试集合同一宿主：WebApplicationFactory 不支持并发创建多个 Host（多个工厂并发触发
/// Program 入口会抛"entry point exited without ever building an IHost"），测试类间必须串行共享。
/// </summary>
[CollectionDefinition("McpHost")]
public class McpHostCollection : ICollectionFixture<McpHostFixture>;
