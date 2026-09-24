using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace DBPilot.Storage.Schema;

/// <summary>
/// 版本化增量迁移执行器（各引擎 SchemaInitializer 启动时调用）：
/// - 迁移脚本随引擎包内嵌（EmbeddedResource），资源名 = {前缀}{文件名}，文件名约定 V{版本}.{序号}__{描述}.sql
///   （如 V0.5.5.01__add_instance_command_timeout.sql；按 Version 数值排序，V0.5.10 晚于 V0.5.9）；
/// - 历史表 dbpilot_schema_version 记录已应用版本（建表 SQL 由引擎方言传入），只执行未应用的脚本；
/// - 一个迁移文件 = 一条批次（整文件单次 ExecuteNonQuery，无分号切分）；
/// - 幂等性：脚本自带方言守卫（SQL Server IF NOT EXISTS / PG IF NOT EXISTS），
///   无守卫能力的方言（MySQL/SQLite）由引擎传入 treatAsApplied 判定"重复列"等已应用错误。
/// schema.sql 保持最新全量口径（新建库一步到位），迁移只负责存量库的版本演进——
/// 新建库跑迁移时命中"已存在"按已应用处理，两端殊途同归。
/// </summary>
public static class SchemaMigrations
{
    /// <summary>资源名里的版本号提取（V0.5.5.01__desc → 0.5.5.1；解析失败按 0.0.0 排最前，让脚本自身报错暴露命名问题）。</summary>
    internal static Version ParseVersion(string resourceName, string prefix)
    {
        var name = resourceName[prefix.Length..];
        var head = name.Split("__")[0];
        return Version.TryParse(head.TrimStart('V', 'v'), out var v) ? v : new Version(0, 0, 0);
    }

    /// <summary>执行全部未应用迁移。createHistoryTableSql 须幂等（IF NOT EXISTS 形态）。</summary>
    public static void Run(
        IDbConnection conn, Assembly assembly, string resourcePrefix,
        string createHistoryTableSql, Func<Exception, bool> treatAsApplied,
        ILogger? logger = null, int commandTimeout = 120)
    {
        using (var create = conn.CreateCommand())
        {
            create.CommandTimeout = commandTimeout;
            create.CommandText = createHistoryTableSql;
            create.ExecuteNonQuery();
        }

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT version FROM dbpilot_schema_version";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                applied.Add(reader.GetString(0));
        }

        var pending = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => ParseVersion(n, resourcePrefix))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var resource in pending)
        {
            // 历史键 = 文件名去扩展（V0.5.5.01__desc）
            var key = resource[resourcePrefix.Length..][..^4];
            if (applied.Contains(key))
                continue;

            string script;
            using (var stream = assembly.GetManifestResourceStream(resource)
                       ?? throw new InvalidOperationException($"未找到内嵌资源 {resource}"))
            using (var reader2 = new StreamReader(stream))
            {
                script = reader2.ReadToEnd();
            }

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = commandTimeout;
                cmd.CommandText = script;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) when (treatAsApplied(ex))
            {
                // 新建库已由 schema.sql 建好同结构（或迁移重复执行）：按已应用处理
                logger?.LogInformation("迁移 {Key} 目标已存在，标记为已应用", key);
            }

            using (var insert = conn.CreateCommand())
            {
                var p = insert.CreateParameter();
                p.ParameterName = "version";
                p.Value = key;
                insert.Parameters.Add(p);
                insert.CommandText = "INSERT INTO dbpilot_schema_version (version) VALUES (@version)";
                insert.ExecuteNonQuery();
            }

            logger?.LogInformation("已应用平台库迁移 {Key}", key);
        }
    }
}
