using System.Security.Cryptography;
using DBPilot.Core.Crypto;

namespace DBPilot.UnitTests.InstanceTests;

public class AesGcmCryptoTests
{
    [Fact]
    public void 加解密往返一致()
    {
        var crypto = new AesGcmCrypto("dbpilot-master-secret");

        var cipher = crypto.Encrypt("P@ssw0rd!中文");

        Assert.StartsWith("v1:", cipher);
        Assert.DoesNotContain("P@ssw0rd", cipher);
        Assert.Equal("P@ssw0rd!中文", crypto.Decrypt(cipher));
    }

    [Fact]
    public void 同明文两次加密_密文不同_随机nonce()
    {
        var crypto = new AesGcmCrypto("k");

        Assert.NotEqual(crypto.Encrypt("same"), crypto.Encrypt("same"));
    }

    [Fact]
    public void 不同主密钥_解密失败()
    {
        var cipher = new AesGcmCrypto("key-A").Encrypt("secret");

        Assert.ThrowsAny<CryptographicException>(() => new AesGcmCrypto("key-B").Decrypt(cipher));
    }

    [Fact]
    public void 密文被篡改_解密失败_认证标签校验()
    {
        var crypto = new AesGcmCrypto("key");
        var cipher = crypto.Encrypt("secret");

        // 翻转密文最后一个字节（tag 区域）
        var payload = Convert.FromBase64String(cipher[3..]);
        payload[^1] ^= 0xFF;
        var tampered = "v1:" + Convert.ToBase64String(payload);

        Assert.ThrowsAny<CryptographicException>(() => crypto.Decrypt(tampered));
    }

    [Theory]
    [InlineData("not-a-cipher")]
    [InlineData("v2:AAAA")]
    [InlineData("v1:AAAA")]   // 长度不足
    public void 非法密文格式_抛格式异常(string input)
        => Assert.Throws<FormatException>(() => new AesGcmCrypto("key").Decrypt(input));
}
