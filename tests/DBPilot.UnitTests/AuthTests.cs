using DBPilot.Core.Auth;

namespace DBPilot.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash后Verify_正确密码通过()
    {
        var hash = PasswordHasher.Hash("dbpilot@2026");
        Assert.StartsWith("pbkdf2$", hash);
        Assert.True(PasswordHasher.Verify("dbpilot@2026", hash));
    }

    [Fact]
    public void Verify_错误密码失败()
    {
        var hash = PasswordHasher.Hash("correct-password");
        Assert.False(PasswordHasher.Verify("wrong-password", hash));
    }

    [Fact]
    public void Verify_格式非法_不抛异常返回false()
    {
        Assert.False(PasswordHasher.Verify("x", ""));
        Assert.False(PasswordHasher.Verify("x", "md5$abc"));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2$abc$!!!$!!!"));
    }

    [Fact]
    public void Hash_两次结果不同_随机盐()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }
}

public class AuthTicketTests
{
    private const string Secret = "test-secret-key-0123456789";

    [Fact]
    public void 创建后校验_返回用户名()
    {
        var ticket = new AuthTicket(Secret);
        var value = ticket.Create("admin", DateTime.UtcNow.AddHours(1));

        Assert.Equal("admin", ticket.Validate(value));
    }

    [Fact]
    public void 过期票据_校验失败()
    {
        var ticket = new AuthTicket(Secret);
        var value = ticket.Create("admin", DateTime.UtcNow.AddSeconds(-1));

        Assert.Null(ticket.Validate(value));
    }

    [Fact]
    public void 篡改payload_校验失败()
    {
        var ticket = new AuthTicket(Secret);
        var value = ticket.Create("admin", DateTime.UtcNow.AddHours(1));
        var parts = value.Split('.');

        // 篡改用户名部分（"admin" → "hacker"），签名不再匹配
        var b64 = parts[0].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        payload = payload.Replace("admin", "hacker");
        var tampered = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(ticket.Validate($"{tampered}.{parts[1]}"));
    }

    [Fact]
    public void 密钥不同_校验失败()
    {
        var value = new AuthTicket(Secret).Create("admin", DateTime.UtcNow.AddHours(1));
        Assert.Null(new AuthTicket("another-secret").Validate(value));
    }

    [Fact]
    public void 非法格式_返回null()
    {
        var ticket = new AuthTicket(Secret);
        Assert.Null(ticket.Validate(""));
        Assert.Null(ticket.Validate("abc"));
        Assert.Null(ticket.Validate("a.b"));
    }
}
