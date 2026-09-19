using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class TryConvertExtensionTests
{
    [Fact]
    public void TryInt_正常与失败()
    {
        Assert.Equal(42, "42".TryInt());
        Assert.Equal(-7, "-7".TryInt());
        Assert.Equal(0, "abc".TryInt());
        Assert.Equal(99, "abc".TryInt(99));
        Assert.Equal(5, ((object?)null).TryInt(5));
    }

    [Fact]
    public void TryLong_正常与失败()
    {
        Assert.Equal(9_000_000_000L, "9000000000".TryLong());
        Assert.Equal(1L, "x".TryLong(1L));
    }

    [Fact]
    public void TryDouble与TryDecimal_正常与失败()
    {
        Assert.Equal(1.5, "1.5".TryDouble());
        Assert.Equal(2.5m, "2.5".TryDecimal());
        Assert.Equal(0.1, "bad".TryDouble(0.1));
        Assert.Equal(0.2m, "bad".TryDecimal(0.2m));
    }

    [Fact]
    public void TryBool_多语义()
    {
        Assert.True("true".TryBool());
        Assert.False("false".TryBool());
        Assert.True("1".TryBool());            // 默认真值
        Assert.False("0".TryBool());           // 默认假值
        Assert.True("yes".TryBool(false, "yes", "no"));
        Assert.False("junk".TryBool());        // 无法识别 → 默认值 false
        Assert.True(((object?)null).TryBool(true));
    }

    [Fact]
    public void TryDateTime_默认与格式化()
    {
        var expected = new DateTime(2026, 8, 20);
        Assert.Equal(expected, "2026-08-20".TryDateTime());
        Assert.Equal(expected, "20/08/2026".TryDateTime("dd/MM/yyyy"));
        Assert.Equal(default, "junk".TryDateTime());
    }

    [Fact]
    public void TryGuid_正常与失败()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        Assert.Equal(guid, guid.ToString().TryGuid());
        Assert.Equal(default, "not-a-guid".TryGuid());
        Assert.Equal(guid, ((object?)null).TryGuid(guid));
    }
}
