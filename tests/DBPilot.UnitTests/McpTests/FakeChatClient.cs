using Microsoft.Extensions.AI;

namespace DBPilot.UnitTests.McpTests;

/// <summary>
/// 脚本化假 IChatClient（MC 测试策略 ③ 层，与 DX-01 ExplainAgent 单测共用同一实现）：
/// 四种行为——① 成功取文本（固定回复）② 超时（挂起直到取消）③ 抛异常 ④ 脚本化多轮调工具
/// （按调用次序出队：前 N-1 轮返回 FunctionCallContent、末轮返回文本结论），并记录每次收到的消息与工具清单。
/// 不发起任何真实 LLM 请求。
/// </summary>
public class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _scripted = new();
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];
    public string LastText { get; private set; } = "";

    /// <summary>行为① 成功：固定文本回复。</summary>
    public static FakeChatClient SucceedsWith(string text) => new() { LastText = text, _finalText = text };

    /// <summary>行为② 超时：挂起直到调用方取消。</summary>
    public static FakeChatClient Hangs() => new() { _hang = true };

    /// <summary>行为③ 异常：直接抛出。</summary>
    public static FakeChatClient Throws(Exception ex) => new() { _exception = ex };

    /// <summary>行为④ 脚本化调工具：依次发起指定的工具调用，最后收到的轮次返回结论文本（参数为 JSON 字符串）。</summary>
    public static FakeChatClient CallsToolsThenConcludes(string conclusion, params (string CallId, string Name, string ArgumentsJson)[] calls)
    {
        var fake = new FakeChatClient { LastText = conclusion, _finalText = conclusion };
        foreach (var (callId, name, json) in calls)
        {
            var args = json is "{}" or "" or null
                ? null
                : (IDictionary<string, object?>?)System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]));
            fake._scripted.Enqueue(response);
        }

        return fake;
    }

    private string? _finalText;
    private bool _hang;
    private Exception? _exception;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToList(), options));

        if (_exception is not null)
            throw _exception;
        if (_hang)
            await Task.Delay(Timeout.Infinite, cancellationToken); // 跟随调用方取消超时

        var response = _scripted.Count > 0 ? _scripted.Dequeue() : new ChatResponse(new ChatMessage(ChatRole.Assistant, _finalText ?? ""));
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? key) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
