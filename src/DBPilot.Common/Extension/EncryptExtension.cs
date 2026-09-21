using System.Security.Cryptography;
using System.Text;

namespace DBPilot.Common;

/// <summary>
/// 加解密扩展方法（移植自 SF.Tookits.EncryptExtension）。
/// 编码统一 UTF-8；ToMd5 大写十六进制、Sha 系列小写十六进制、HmacSha256 小写十六进制。
/// </summary>
public static class EncryptExtension
{
    #region Base64

    /// <summary>Base64 编码。</summary>
    public static string ToBase64(this string? inputStr)
        => inputStr.IsNullOrEmpty() ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(inputStr));

    /// <summary>Base64 解码。</summary>
    public static string FromBase64(this string? base64String)
        => base64String.IsNullOrEmpty() ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(base64String));

    #endregion

    #region 散列

    /// <summary>MD5 散列（大写十六进制）。</summary>
    public static string ToMd5(this string? inputStr)
    {
        if (inputStr.IsNullOrEmpty())
            return string.Empty;

        var data = MD5.HashData(Encoding.UTF8.GetBytes(inputStr));
        return Convert.ToHexString(data);
    }

    /// <summary>SHA1 散列（小写十六进制）。</summary>
    public static string ToSha1(this string? inputStr)
    {
        if (inputStr.IsNullOrEmpty())
            return string.Empty;

        var data = SHA1.HashData(Encoding.UTF8.GetBytes(inputStr));
        return Convert.ToHexString(data).ToLowerInvariant();
    }

    /// <summary>SHA256 散列（小写十六进制）。</summary>
    public static string ToSha256(this string? inputStr)
    {
        if (inputStr.IsNullOrEmpty())
            return string.Empty;

        var data = SHA256.HashData(Encoding.UTF8.GetBytes(inputStr));
        return Convert.ToHexString(data).ToLowerInvariant();
    }

    /// <summary>SHA256 散列前缀（取前 bytes 字节，小写十六进制）：SQL/事件指纹短哈希统一口径（如 8 字节 = 16 hex）。</summary>
    public static string Sha256PrefixHex(this string? inputStr, int bytes = 8)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputStr ?? string.Empty)).AsSpan(0, bytes)).ToLowerInvariant();

    /// <summary>HMAC-SHA256（密钥为 UTF-8 字节，小写十六进制）。</summary>
    public static string ToHmacSha256(this string? inputStr, string secret)
    {
        if (inputStr.IsNullOrEmpty())
            return string.Empty;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var data = hmac.ComputeHash(Encoding.UTF8.GetBytes(inputStr));
        return Convert.ToHexString(data).ToLowerInvariant();
    }

    #endregion
}
