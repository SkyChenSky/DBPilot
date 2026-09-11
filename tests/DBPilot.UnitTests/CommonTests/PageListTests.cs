using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class PageListTests
{
    [Fact]
    public void PageList_Total为0_无翻页()
    {
        var page = PageList<int>.Create([], 0, 1, 10);

        Assert.Equal(0, page.TotalPage);
        Assert.False(page.HasPrev);
        Assert.False(page.HasNext);
    }

    [Fact]
    public void PageList_整除_页码边界()
    {
        var page1 = PageList<int>.Create([1, 2], 20, 1, 10);
        Assert.Equal(2, page1.TotalPage);
        Assert.False(page1.HasPrev);
        Assert.True(page1.HasNext);

        var page2 = PageList<int>.Create([3, 4], 20, 2, 10);
        Assert.True(page2.HasPrev);
        Assert.False(page2.HasNext);
    }

    [Fact]
    public void PageList_非整除_进一()
    {
        var page = PageList<int>.Create([1], 25, 3, 10);
        Assert.Equal(25, page.Total);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(3, page.TotalPage);
    }

    [Fact]
    public void PageList_Empty_默认页码()
    {
        var page = PageList<string>.Empty();
        Assert.Equal(1, page.PageIndex);
        Assert.Equal(20, page.PageSize);
        Assert.Empty(page.Items);
    }
}

public class PageListParamsTests
{
    [Fact]
    public void 默认值_Page1_Limit10()
    {
        var p = new PageListParams();
        Assert.Equal(1, p.Page);
        Assert.Equal(10, p.Limit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(5, 5)]
    public void Page_非法值夹取为1(int input, int expected)
    {
        var p = new PageListParams { Page = input };
        Assert.Equal(expected, p.Page);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(50, 50)]
    [InlineData(201, 200)]
    [InlineData(99999, 200)]
    public void Limit_夹取在1到200(int input, int expected)
    {
        var p = new PageListParams { Limit = input };
        Assert.Equal(expected, p.Limit);
    }
}
