using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class StringExtensionTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", false)]
    [InlineData("abc", false)]
    public void IsNullOrEmpty_各输入(string? input, bool expected)
        => Assert.Equal(expected, input.IsNullOrEmpty());

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("user.name+tag@domain.co", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEmail_常见格式(string? input, bool expected)
        => Assert.Equal(expected, input.IsEmail());

    [Theory]
    [InlineData("13812345678", true)]
    [InlineData("22345678901", false)]   // 2 开头
    [InlineData("1381234567", false)]    // 10 位
    [InlineData("", false)]
    public void IsMobile_大陆手机号(string? input, bool expected)
        => Assert.Equal(expected, input.IsMobile());

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData(null, false)]
    public void IsIp_合法与非法(string? input, bool expected)
        => Assert.Equal(expected, input.IsIp());

    [Theory]
    [InlineData("123", true)]
    [InlineData("-1.5", true)]
    [InlineData("1e5", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void IsNumeric_整数与小数(string? input, bool expected)
        => Assert.Equal(expected, input.IsNumeric());

    [Fact]
    public void Sub_长度足够_截取前缀()
        => Assert.Equal("ab", "abcdef".Sub(2));

    [Fact]
    public void Sub_长度不足_返回原串()
        => Assert.Equal("abc", "abc".Sub(10));

    [Fact]
    public void Sub_空串安全()
        => Assert.Equal("", ((string?)null).Sub(3));

    [Fact]
    public void FmtMobile_中间四位脱敏()
        => Assert.Equal("138****5678", "13812345678".FmtMobile());

    [Fact]
    public void FmtMobile_短号码原样返回()
        => Assert.Equal("1234567", "1234567".FmtMobile());
}
