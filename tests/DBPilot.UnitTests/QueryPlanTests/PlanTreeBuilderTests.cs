using System.Text;
using DBPilot.Core.QueryPlan;

namespace DBPilot.UnitTests.QueryPlanTests;

/// <summary>计划树解析（PlanTreeBuilder.Build 纯函数）测试。</summary>
public class PlanTreeBuilderTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Wrap(string relOpXml) =>
        $"""<?xml version="1.0" encoding="utf-16"?><ShowPlanXML xmlns="{Ns}" Version="1.481"><BatchSequence><Batch><Statements>{relOpXml}</Statements></Batch></BatchSequence></ShowPlanXML>""";

    /// <summary>Nested Loops(Clustered Index Seek → Index Scan) 两层样例。</summary>
    private const string SamplePlan = """
        <StmtSimple StatementText="SELECT b FROM dbo.T WHERE c = @p">
          <QueryPlan DegreeOfParallelism="1">
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10.5" EstimateExecutions="1" EstimatedTotalSubtreeCost="0.05">
              <NestedLoops>
                <OuterReferences><ColumnReference Database="db" Table="T" Column="id"/></OuterReferences>
                <RelOp NodeId="1" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1" EstimateExecutions="1" SubtreeCost="0.003">
                  <IndexSeek Ordered="true">
                    <SeekPredicates><SeekPredicateNew><RangeColumns><ColumnReference Database="db" Table="T" Column="id"/></RangeColumns></SeekPredicateNew></SeekPredicates>
                    <Object Database="db" Schema="dbo" Table="T" Index="PK_T" />
                  </IndexSeek>
                </RelOp>
                <RelOp NodeId="2" PhysicalOp="Index Scan" LogicalOp="Index Scan" EstimateRows="50000" EstimateExecutions="1" SubtreeCost="1.2" Parallel="true">
                  <IndexScan Ordered="false">
                    <Object Database="db" Schema="dbo" Table="U" Index="IX_U_c"/>
                    <Predicate>
                      <ScalarOperator ScalarString="[db].[dbo].[U].[c]=[@p]"/>
                    </Predicate>
                  </IndexScan>
                </RelOp>
              </NestedLoops>
            </RelOp>
          </QueryPlan>
        </StmtSimple>
        """;

    [Fact]
    public void 解析树结构_ReloOp递归与属性提取()
    {
        var trees = PlanTreeBuilder.Build(Wrap(SamplePlan));

        var root = Assert.Single(trees);
        Assert.Equal("Nested Loops", root.PhysicalOp);
        Assert.Equal("Inner Join", root.LogicalOp);
        Assert.Equal(0.05m, root.SubtreeCost);
        Assert.Equal(10.5m, root.EstimateRows);
        Assert.Equal(2, root.Children.Count);

        var seek = root.Children[0];
        Assert.Equal("Clustered Index Seek", seek.PhysicalOp);
        Assert.Equal("[db].[dbo].[T].[PK_T]", seek.ObjectName);
        Assert.Equal(1, seek.EstimateExecutions);          // Rebinds/Rewinds 缺省 0 → 0+0+1
        Assert.Contains("SeekPredicateNew", seek.SeekPredicates);

        var scan = root.Children[1];
        Assert.Equal("Index Scan", scan.PhysicalOp);
        Assert.Equal("[db].[dbo].[U].[IX_U_c]", scan.ObjectName);
        Assert.Contains("[@p]", scan.Predicate);
        Assert.True(scan.Parallel);
        Assert.False(seek.Parallel);
    }

    [Fact]
    public void 多语句_返回多棵树()
    {
        var xml = Wrap(SamplePlan + SamplePlan.Replace("dbo.T", "dbo.T2"));
        var trees = PlanTreeBuilder.Build(xml);
        Assert.Equal(2, trees.Count);
    }

    [Fact]
    public void 空与非法XML_返回空列表()
    {
        Assert.Empty(PlanTreeBuilder.Build(null));
        Assert.Empty(PlanTreeBuilder.Build(""));
        Assert.Empty(PlanTreeBuilder.Build("<not-a-plan/>"));
    }

    [Fact]
    public void 超长谓词_截断约200字符()
    {
        var longPred = new string('x', 500);
        var relOp = $"""
            <StmtSimple><QueryPlan><RelOp NodeId="0" PhysicalOp="Filter" LogicalOp="Filter" Predicate="{longPred}">
              <Filter/></RelOp></QueryPlan></StmtSimple>
            """;
        var trees = PlanTreeBuilder.Build(Wrap(relOp));

        var node = Assert.Single(trees);
        Assert.True(node.Predicate!.Length <= 202);   // 200 + 省略号
        Assert.EndsWith("…", node.Predicate);
    }

    [Fact]
    public void Spill告警_属性与子元素两形态均识别()
    {
        var relOp = """
            <StmtSimple><QueryPlan>
              <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort" Warnings="SpillToTempDb:1">
                <Sort><Warnings><SpillToTempDb SpilledData="100"/></Warnings></Sort>
              </RelOp>
              <RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort">
                <Sort><Warnings><SpillToTempDb SpilledData="50"/></Warnings></Sort>
              </RelOp>
            </QueryPlan></StmtSimple>
            """;
        var trees = PlanTreeBuilder.Build(Wrap(relOp));

        Assert.True(trees[0].SpillWarning);           // 属性形态
        Assert.True(trees[1].SpillWarning);           // 子元素形态
    }
}
