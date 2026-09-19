using System.Globalization;
using System.Xml.Linq;

namespace DBPilot.Core.QueryPlan;

/// <summary>计划树节点（RelOp 递归结构，前端折叠树直接序列化）。</summary>
public class PlanTreeNode
{
    public string PhysicalOp { get; set; } = "";
    public string LogicalOp { get; set; } = "";
    public decimal SubtreeCost { get; set; }
    public decimal EstimateRows { get; set; }
    public decimal EstimateExecutions { get; set; }

    /// <summary>IndexScan/Seek 等的 Object="[db].[tbl].[idx]"。</summary>
    public string? ObjectName { get; set; }

    /// <summary>谓词 / Seek 谓词（截断 ~200 字符，全文在 XML tab）。</summary>
    public string? Predicate { get; set; }
    public string? SeekPredicates { get; set; }

    /// <summary>Parallel="true" / Warnings 含 Spill（含子元素形态 SpillToTempDb 等）。</summary>
    public bool Parallel { get; set; }
    public bool SpillWarning { get; set; }

    public List<PlanTreeNode> Children { get; set; } = [];
}

/// <summary>
/// ShowPlan XML → 递归计划树（请求时解析，不落库）：
/// StmtSimple → QueryPlan → RelOp 递归展开。一个计划 XML 可含多语句 → 返回语句级树列表
/// （每语句取 QueryPlan 下全部根 RelOp）。
/// </summary>
public static class PlanTreeBuilder
{
    private const int MaxTextChars = 200;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>解析计划 XML；空串/格式错误返回空列表（调用方按"XML 缺失"展示）。</summary>
    internal static List<PlanTreeNode> Build(string? planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml)) return [];

        try
        {
            var doc = XDocument.Parse(planXml);
            var trees = new List<PlanTreeNode>();

            // StmtSimple 为语句级；StmtCond（IF/WHILE）内部无 QueryPlan，跳过
            foreach (var stmt in doc.Descendants(Ns + "StmtSimple"))
            {
                var queryPlan = stmt.Element(Ns + "QueryPlan");
                if (queryPlan is null) continue;

                foreach (var relOp in queryPlan.Elements(Ns + "RelOp"))
                    trees.Add(ToNode(relOp));
            }

            return trees;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static PlanTreeNode ToNode(XElement relOp)
    {
        var node = new PlanTreeNode
        {
            PhysicalOp = (string?)relOp.Attribute("PhysicalOp") ?? "",
            LogicalOp = (string?)relOp.Attribute("LogicalOp") ?? "",
            SubtreeCost = AttrDecimal(relOp, "EstimatedTotalSubtreeCost"),
            EstimateRows = AttrDecimal(relOp, "EstimateRows"),
            // RelOp 无 EstimateExecutions 属性（SSMS 口径 = Rebinds + Rewinds + 1）
            EstimateExecutions = AttrDecimal(relOp, "EstimateRebinds") + AttrDecimal(relOp, "EstimateRewinds") + 1,
            Predicate = Truncate(PredicateText(relOp)),
            Parallel = string.Equals((string?)relOp.Attribute("Parallel"), "true", StringComparison.OrdinalIgnoreCase),
        };

        // Warnings：Spill 可能是属性（如 "SpillToTempDb..."）或 Warnings 子元素形态（RelOp 直接子级或算子元素内）
        var warnAttr = (string?)relOp.Attribute("Warnings");
        node.SpillWarning = (warnAttr != null && warnAttr.Contains("Spill", StringComparison.OrdinalIgnoreCase))
            || relOp.Descendants(Ns + "Warnings")
                .SelectMany(w => w.Elements())
                .Any(e => e.Name.LocalName.Contains("Spill", StringComparison.OrdinalIgnoreCase));

        // 对象：算子元素内 <Object> 的 Database/Schema/Table/Index 属性（值本身多已带 []，剥壳后统一拼装）
        var obj = relOp.Descendants(Ns + "Object").FirstOrDefault();
        if (obj != null)
        {
            var parts = new[] { (string?)obj.Attribute("Database"), (string?)obj.Attribute("Schema"),
                                (string?)obj.Attribute("Table"), (string?)obj.Attribute("Index") }
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!.Trim('[', ']'))
                .ToList();
            if (parts.Count > 0)
                node.ObjectName = Truncate($"[{string.Join("].[", parts)}]");
        }

        // Seek 谓词：优先取 RangeExpressions 的 ScalarString（SSMS 表达式形态），兜底元素原文
        node.SeekPredicates = Truncate(relOp.Descendants(Ns + "SeekPredicates")
            .Descendants(Ns + "ScalarOperator")
            .Select(s => (string?)s.Attribute("ScalarString"))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x))
            ?? relOp.Descendants(Ns + "SeekPredicates").FirstOrDefault()?.ToString());

        // 子 RelOp 位于算子元素（NestedLoops/IndexScan...）内部，非本 RelOp 直接子级；只下探一层算子元素，
        // 深层 RelOp 由递归继续展开
        foreach (var child in relOp.Elements().SelectMany(op => op.Elements(Ns + "RelOp")))
            node.Children.Add(ToNode(child));

        return node;
    }

    /// <summary>谓词文本：RelOp 属性直取（老形态）；否则取 Predicate 子元素下首个 ScalarOperator 的
    /// ScalarString（SSMS 同款表达式），再兜底元素原文。</summary>
    private static string? PredicateText(XElement relOp)
    {
        var attr = (string?)relOp.Attribute("Predicate");
        if (attr != null) return attr;

        var scalar = relOp.Descendants(Ns + "Predicate")
            .SelectMany(p => p.Descendants(Ns + "ScalarOperator"))
            .Select(s => (string?)s.Attribute("ScalarString"))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x));
        if (scalar != null) return scalar;

        return relOp.Descendants(Ns + "Predicate").FirstOrDefault()?.ToString();
    }

    private static decimal AttrDecimal(XElement e, string name)
        => decimal.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string? Truncate(string? s)
        => s is null ? null : (s.Length <= MaxTextChars ? s : s[..MaxTextChars] + "…");
}
