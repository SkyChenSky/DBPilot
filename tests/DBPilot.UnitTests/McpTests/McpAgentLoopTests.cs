using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace DBPilot.UnitTests.McpTests;

/// <summary>
/// MCP Agent 回路测（测试策略 ③ 层——提前验证 DX 高配 Agent 的工具注入机制）：
/// MCP 客户端枚举工具（McpClientTool 本身即 AIFunction）挂进 ChatOptions.Tools →
/// fake IChatClient 脚本化三步（决定调工具 → 拿到结果 → 产出结论），
/// UseFunctionInvocation 中间件负责真正调 /mcp 并把结果回灌对话。无需真实 LLM。
/// </summary>
[Collection("McpHost")]
public class McpAgentLoopTests(McpHostFixture host)
{
    private readonly McpHostFixture _host = host;

    [Fact]
    public async Task Agent三步回路_工具应被真实调用且结果进入对话上下文()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var tools = await mcp.ListToolsAsync();

        // fake agent 脚本：第一轮决定调 list_instances，第二轮基于函数结果产出结论
        var fake = FakeChatClient.CallsToolsThenConcludes(
            "已获取实例清单（平台库未配置，无实例可查）。",
            (CallId: "call_1", Name: "list_instances", ArgumentsJson: "{}"));

        // 与 DX ExplainAgent 同款机制：工具挂 ChatOptions.Tools + 函数调用中间件
        var chat = fake.AsBuilder().UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [.. tools] };
        var response = await chat.GetResponseAsync("这个平台接了哪些实例？", options);

        // ① 结论由最后一轮产出
        Assert.Contains("实例", response.Text);

        // ② fake 被调了两轮：第一轮发起工具调用，第二轮收到函数结果
        Assert.Equal(2, fake.Calls.Count);

        // ③ 工具真的被调用了：第二轮的对话上下文里有 list_instances 的函数结果（经真实 /mcp 往返）
        var secondTurn = fake.Calls[1].Messages;
        var resultContent = secondTurn.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == "call_1");
        var resultJson = resultContent.Result?.ToString();
        Assert.NotNull(resultJson);
        Assert.Contains("error", resultJson); // 平台库未配置 → error 载荷经 MCP 全链回到对话上下文
    }

    [Fact]
    public async Task Agent回路_工具清单已注入ChatOptions()
    {
        await using var mcp = await _host.CreateMcpClientAsync();
        var tools = await mcp.ListToolsAsync();

        var fake = FakeChatClient.SucceedsWith("直接回答，无需工具。");
        var chat = fake.AsBuilder().Build();

        await chat.GetResponseAsync("你好", new ChatOptions { Tools = [.. tools] });

        var injected = fake.Calls.Single().Options?.Tools;
        Assert.NotNull(injected);
        Assert.Contains(injected, t => t.Name == "list_instances");
        // McpClientTool 即 AIFunction 子类——MCP 工具可零转换挂进 ME.AI 工具清单（DX 高配复用的机制）
        Assert.All(injected, t => Assert.IsAssignableFrom<AIFunction>(t));
    }
}
