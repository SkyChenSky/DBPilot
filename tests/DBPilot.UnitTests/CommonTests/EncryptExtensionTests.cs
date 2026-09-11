using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class EncryptExtensionTests
{
    [Fact]
    public void ToBase64_FromBase64_往返一致()
    {
        Assert.Equal("aGVsbG8=", "hello".ToBase64());
        Assert.Equal("hello", "aGVsbG8=".FromBase64());
    }

    [Fact]
    public void ToBase64_空串安全()
    {
        Assert.Equal("", ((string?)null).ToBase64());
        Assert.Equal("", "".ToBase64());
    }

    [Fact]
    public void ToMd5_已知向量()
        => Assert.Equal("900150983CD24FB0D6963F7D28E17F72", "abc".ToMd5());

    [Fact]
    public void ToSha1_已知向量()
        => Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", "abc".ToSha1());

    [Fact]
    public void ToSha256_已知向量()
        => Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "abc".ToSha256());

    [Fact]
    public void ToHmacSha256_已知向量()
        => Assert.Equal(
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            "The quick brown fox jumps over the lazy dog".ToHmacSha256("key"));
}
