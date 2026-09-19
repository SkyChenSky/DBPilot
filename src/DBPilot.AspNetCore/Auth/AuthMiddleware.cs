using DBPilot.Common;
using DBPilot.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace DBPilot.AspNetCore.Auth;

public static class AuthMiddlewareExtensions
{
    /// <summary>
    /// 最简认证：拦截 /api/**（/api/login、/api/health 放行），
    /// 校验签名 Cookie，未登录返回 401 统一响应。
    /// </summary>
    public static IApplicationBuilder UseSimpleAuth(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var isApi = path.StartsWithSegments("/api");
            var isOpen = path.StartsWithSegments("/api/login")
                         || path.StartsWithSegments("/api/health");
            if (isApi && !isOpen)
            {
                var options = ctx.RequestServices.GetRequiredService<AuthOptions>();
                var ticket = ctx.RequestServices.GetRequiredService<AuthTicket>();
                var cookie = ctx.Request.Cookies[options.CookieName];
                var user = cookie is null ? null : ticket.Validate(cookie);
                if (user is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync(ApiResponse.Fail("未登录或会话已过期", 401).ToJson());
                    return;
                }

                ctx.Items["Username"] = user;
            }

            await next();
        });
    }
}
