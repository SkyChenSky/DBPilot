using DBPilot.Common;
using DBPilot.Core.Providers;

namespace DBPilot.Core.Indexes;

/// <summary>
/// 索引诊断脚本/判定纯函数（脚本生成、未使用判定）。
/// DMV 列清单格式为 "[Col1], [Col2]"（已带括号），statement 形如 "[dbo].[Table]"。
/// </summary>
public static class IndexScriptBuilder
{
    private const int MaxIndexNameLength = 128;

    /// <summary>"[A], [B]" → ["A", "B"]；空串/null → 空表。</summary>
    public static List<string> ParseColumns(string? columns)
    {
        if (columns.IsNullOrWhiteSpace())
            return [];

        return columns
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Trim('[', ']'))
            .Where(c => !c.IsNullOrWhiteSpace())
            .ToList();
    }

    /// <summary>
    /// 建议键列（等值+不等值）是现有索引键列的前缀 → 建议已被覆盖。
    /// 用于过滤 DMV 残留条目（用户已按建议建索引，但 DMV 条目未清除会残留到内存压力/重启）。
    /// 反向不算覆盖：建议 (A,B) vs 现有 (A) —— B 列仍未覆盖，建议保留。
    /// </summary>
    public static bool IsCoveredByExisting(string? equalityColumns, string? inequalityColumns, string? existingKeyColumns)
    {
        var suggestion = ParseColumns(equalityColumns).Select(c => c.ToLowerInvariant()).ToList();
        suggestion.AddRange(ParseColumns(inequalityColumns).Select(c => c.ToLowerInvariant()));
        var existing = ParseColumns(existingKeyColumns).Select(c => c.ToLowerInvariant()).ToList();

        return suggestion.Count > 0
               && suggestion.Count <= existing.Count
               && suggestion.Zip(existing, (a, b) => a == b).All(eq => eq);
    }

    /// <summary>列清单 → 键列子句：重排括号并去空白（"[A], [B]" 或 "A, B" 均可）。</summary>
    public static string ToKeyClause(string? columns)
    {
        var list = ParseColumns(columns);
        return string.Join(", ", list.Select(c => $"[{c}]"));
    }

    /// <summary>"[dbo].[Orders]" → "Orders"（索引名用短表名）。</summary>
    public static string ExtractShortTableName(string statement)
    {
        var last = statement.Split(']')
            .Select(s => s.Trim())
            .Where(s => !s.IsNullOrWhiteSpace())
            .DefaultIfEmpty(statement)
            .Last()
            .TrimStart('.', '[')
            .TrimEnd(']');

        return last.IsNullOrWhiteSpace() ? statement : last;
    }

    /// <summary>索引名：IX_表_等值列_不等值列，超 128 字符截断并追加短哈希防撞。</summary>
    public static string BuildIndexName(string tableName, string? equalityColumns, string? inequalityColumns)
    {
        var parts = new List<string> { "IX", ExtractShortTableName(tableName) };
        parts.AddRange(ParseColumns(equalityColumns));
        parts.AddRange(ParseColumns(inequalityColumns));

        var name = string.Join("_", parts);
        if (name.Length <= MaxIndexNameLength)
            return name;

        // 截断 + 8 位哈希后缀，保证唯一性概率（ToMd5 大写十六进制，与原内联口径一致）
        var hash = name.ToMd5()[..8];
        return $"{name[..(MaxIndexNameLength - 9)]}_{hash}";
    }

    /// <summary>
    /// CREATE INDEX 脚本。onlineSupported 仅当 Edition 为 Enterprise 时为 true。
    /// 键列 = 等值列 + 不等值列；包含列单独 INCLUDE。
    /// </summary>
    public static string BuildCreateIndexSql(string tableName, string? equalityColumns,
        string? inequalityColumns, string? includedColumns, bool onlineSupported)
    {
        var keyClause = string.Join(", ", new[] { ToKeyClause(equalityColumns), ToKeyClause(inequalityColumns) }
            .Where(s => !s.IsNullOrEmpty()));

        var sql = $"CREATE INDEX [{BuildIndexName(tableName, equalityColumns, inequalityColumns)}] ON {tableName} ({keyClause})";
        var includeClause = ToKeyClause(includedColumns);
        if (!includeClause.IsNullOrEmpty())
            sql += $" INCLUDE ({includeClause})";

        if (onlineSupported)
            sql += " WITH (ONLINE = ON)";

        return sql + ";";
    }

    /// <summary>未使用索引：无读（seeks+scans+lookups=0）且写放大明显（updates&gt;10000、页数&gt;1000）；
    /// 主键/唯一索引自动排除；页数未知（null，MySQL 无每索引页数）不排除
    /// —— 写放大上万且零读已是强信号。</summary>
    public static bool IsUnused(IndexUsageItem i, long minUpdates = 10000, long minPages = 1000)
        => !i.IsPrimaryKey
           && !i.IsUnique
           && i.UserSeeks + i.UserScans + i.UserLookups == 0
           && i.UserUpdates > minUpdates
           && (i.UsedPageCount is null || i.UsedPageCount > minPages);

    /// <summary>未使用索引保守处置脚本（DISABLE 不删数据，观察期后可 REBUILD 恢复）。</summary>
    public static string BuildDisableSql(IndexUsageItem i)
        => BuildDisableSql(i.TableName, i.IndexName);

    /// <summary>未使用索引保守处置脚本（快照行重载：TableName 形如 [dbo].[Orders]）。</summary>
    public static string BuildDisableSql(string tableName, string indexName)
        => $"ALTER INDEX [{indexName}] ON {tableName} DISABLE;";

    /// <summary>未使用索引保守处置脚本单入口（按被监控实例引擎选方言）：SQL Server ALTER INDEX DISABLE；
    /// MySQL 8.0+ ALTER INDEX INVISIBLE；PostgreSQL 无对等口径返回 null（前端已藏按钮，此处兜底）。
    /// TableName 由 Provider 全限定（SS [dbo].[T] / MySQL `库`.`表`）。</summary>
    public static string? BuildDisableSql(string engine, string tableName, string indexName)
        => string.Equals(engine, DbpilotEngines.MySql, StringComparison.OrdinalIgnoreCase)
            ? BuildMySqlDisableSql(tableName, indexName)
            : string.Equals(engine, DbpilotEngines.PostgreSql, StringComparison.OrdinalIgnoreCase)
                ? null
                : BuildDisableSql(tableName, indexName);

    /// <summary>未使用索引 MySQL 口径（8.0+）：索引对优化器不可见（可恢复 VISIBLE，不删数据），
    /// 等价 DISABLE 的保守观察语义。</summary>
    private static string BuildMySqlDisableSql(string tableName, string indexName)
        => $"ALTER TABLE {tableName} ALTER INDEX `{indexName}` INVISIBLE;";

    // ---------------- 索引碎片 ----------------

    /// <summary>碎片率 ≥ 此值 → REORGANIZE（微软建议区间 10%~30%）。</summary>
    public const double FragReorganizeThreshold = 10;

    /// <summary>碎片率 &gt; 此值 → REBUILD（微软建议 &gt;30%）。</summary>
    public const double FragRebuildThreshold = 30;

    /// <summary>页数 &lt; 此值不建议处理（收益低于维护成本）。</summary>
    public const long FragMinPagesNoAction = 1000;

    /// <summary>碎片处置建议：&gt;30% REBUILD；10%~30%（含 10、含 30）REORGANIZE；否则无需处理。</summary>
    public static FragAction RecommendAction(double fragPercent)
        => fragPercent > FragRebuildThreshold ? FragAction.Rebuild
            : fragPercent >= FragReorganizeThreshold ? FragAction.Reorganize
            : FragAction.None;

    /// <summary>
    /// 碎片处置脚本：REBUILD（Enterprise 加 ONLINE=ON）/ REORGANIZE（本身即在线操作，无 ONLINE 选项）；
    /// 分区索引（partition_number &gt; 1）只处理目标分区。FragAction.None 返回空串。
    /// </summary>
    public static string BuildFragScript(string tableName, string indexName, FragAction action,
        bool onlineSupported, int partitionNumber)
    {
        if (action == FragAction.None)
            return "";
        var partition = partitionNumber > 1 ? $" PARTITION = {partitionNumber}" : "";
        return action == FragAction.Rebuild
            ? $"ALTER INDEX [{indexName}] ON {tableName} REBUILD{partition}{(onlineSupported ? " WITH (ONLINE = ON)" : "")};"
            : $"ALTER INDEX [{indexName}] ON {tableName} REORGANIZE{partition};";
    }
}
