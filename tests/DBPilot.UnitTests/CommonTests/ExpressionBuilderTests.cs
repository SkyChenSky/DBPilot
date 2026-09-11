using DBPilot.Common;

namespace DBPilot.UnitTests.CommonTests;

public class ExpressionBuilderTests
{
    private static readonly List<int> Data = [1, 3, 5, 7, 9, 11];

    [Fact]
    public void Init_恒真条件()
    {
        var predicate = ExpressionBuilder.Init<int>().Compile();

        Assert.All(Data, x => Assert.True(predicate(x)));
    }

    [Fact]
    public void And_交集()
    {
        var predicate = ExpressionBuilder.Init<int>()
            .And(x => x > 3)
            .And(x => x < 9)
            .Compile();

        Assert.Equal([5, 7], Data.Where(predicate));
    }

    [Fact]
    public void Or_并集()
    {
        var predicate = ExpressionBuilder.Init<int>()
            .And(x => x < 3)
            .Or(x => x > 9)
            .Compile();

        Assert.Equal([1, 11], Data.Where(predicate));
    }

    [Fact]
    public void 组合条件_复杂表达式()
    {
        var predicate = ExpressionBuilder.Init<int>()
            .And(x => x % 2 == 1)
            .And(x => x > 2)
            .Or(x => x == 1)
            .Compile();

        // 等价于 (奇数 且 >2) 或 1 → 全部命中（数据本身全为奇数）
        Assert.Equal(Data.Count, Data.Count(predicate));
    }
}
