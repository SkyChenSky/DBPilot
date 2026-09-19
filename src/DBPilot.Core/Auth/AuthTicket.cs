using System.Security.Cryptography;
using System.Text;

namespace DBPilot.Core.Auth;

/// <summary>
/// HMAC-SHA256 签名的登录票据。
/// 格式：base64url(payload) + "." + base64url(hmac)，payload = "username|expiryUnixSeconds"。
/// </summary>
public class AuthTicket(string secretKey)
{
    public string Create(string username, DateTime expiresUtc)
    {
        var payload = $"{username}|{new DateTimeOffset(expiresUtc).ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var sig = Sign(payloadBytes);
        return $"{Base64Url(payloadBytes)}.{Base64Url(sig)}";
    }

    /// <summary>校验签名与过期时间；通过返回用户名，否则返回 null。</summary>
    public string? Validate(string ticket)
    {
        var dot = ticket.IndexOf('.');
        if (dot <= 0 || dot == ticket.Length - 1)
            return null;

        byte[] payloadBytes;
        byte[] sig;
        try
        {
            payloadBytes = Base64UrlDecode(ticket[..dot]);
            sig = Base64UrlDecode(ticket[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = Sign(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(sig, expected))
            return null;

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var sep = payload.LastIndexOf('|');
        if (sep <= 0 || !long.TryParse(payload[(sep + 1)..], out var expiry))
            return null;

        if (DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime <= DateTime.UtcNow)
            return null;

        return payload[..sep];
    }

    private byte[] Sign(byte[] payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return hmac.ComputeHash(payload);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
