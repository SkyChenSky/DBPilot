using System.Security.Cryptography;
using System.Text;

namespace DBPilot.AspNetCore.Mcp;

/// <summary>
/// MCP 出口机器认证：只拦 /mcp 前缀，请求头 X-Api-Key 与配置
/// DBPilot:Modules:Mcp:ApiKey 定长时间比较，不匹配 401——不碰 /api 的 Cookie 认证线。
/// </summary>
public class McpAuthMiddleware(RequestDelegate next, McpOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            var key = context.Request.Headers["X-Api-Key"].ToString();
            if (!FixedTimeEquals(key, options.ApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context);
    }

    /// <summary>定长时间比较（防时序侧信道；长度不同也走完同样时长的哈希比较）。</summary>
    private static bool FixedTimeEquals(string provided, string expected)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
