using DBPilot.Common;
using DBPilot.Core.Auth;
using DBPilot.AspNetCore.Auth;
using Microsoft.AspNetCore.Mvc;

namespace DBPilot.AspNetCore.Controllers;

public record LoginRequest(string? Username, string? Password);

[Route("api")]
public class AuthController(AuthOptions options, AuthTicket ticket) : ApiControllerBase
{
    private readonly AuthOptions _options = options;
    private readonly AuthTicket _ticket = ticket;

    /// <summary>登录：校验通过后下发签名 Cookie。</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username)
            || string.IsNullOrEmpty(request.Password)
            || !string.Equals(request.Username, _options.Username, StringComparison.OrdinalIgnoreCase)
            || !PasswordHasher.Verify(request.Password, _options.PasswordHash))
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse.Fail("用户名或密码错误", 401));
        }

        var expires = DateTime.UtcNow.AddHours(Math.Max(1, _options.ExpiresHours));
        var cookieValue = _ticket.Create(_options.Username, expires);
        Response.Cookies.Append(_options.CookieName, cookieValue, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires,
        });

        return Ok(ApiResponse.Ok(new { username = _options.Username }));
    }

    /// <summary>退出：删除 Cookie。</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(_options.CookieName, new CookieOptions { Path = "/" });
        return Ok(ApiResponse.Ok());
    }

    /// <summary>当前登录人（路由守卫探测会话用）。</summary>
    [HttpGet("me")]
    public IActionResult Me()
    {
        var username = (string?)HttpContext.Items["Username"] ?? _options.Username;
        return Ok(ApiResponse.Ok(new { username }));
    }
}
