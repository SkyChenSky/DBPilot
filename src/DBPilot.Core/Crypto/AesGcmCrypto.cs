using System.Security.Cryptography;
using System.Text;

namespace DBPilot.Core.Crypto;

/// <summary>
/// 实例凭据 AES-GCM 加解密。
/// 密文格式：v1:Base64(12B nonce ‖ ciphertext + 16B tag)。
/// 主密钥来自 DBPilot:Auth:Secret / DBPILOT_MASTER_KEY（与登录签名共用一套密钥解析），
/// 经 SHA-256 派生 32 字节 AES 密钥；解密仅发生在内存，日志统一脱敏。
/// </summary>
public class AesGcmCrypto(string masterSecret)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string VersionPrefix = "v1:";

    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(masterSecret));

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        return VersionPrefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Decrypt(string cipherText)
    {
        if (!cipherText.StartsWith(VersionPrefix, StringComparison.Ordinal))
            throw new FormatException("凭据密文格式不正确");

        var payload = Convert.FromBase64String(cipherText[VersionPrefix.Length..]);
        if (payload.Length < NonceSize + TagSize)
            throw new FormatException("凭据密文长度不合法");

        var nonce = payload[..NonceSize];
        var cipher = payload[NonceSize..^TagSize];
        var tag = payload[^TagSize..];
        var plaintext = new byte[cipher.Length];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
