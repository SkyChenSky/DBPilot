using System.Text.RegularExpressions;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DBPilot.PostgreSql;

/// <summary>
/// PostgreSQL 平台库初始化：
/// 1)（可选）建库（PG 无 CREATE DATABASE IF NOT EXISTS——查 pg_database 不存在才建，
///    连 postgres 维护库执行；CREATE DATABASE 不能在事务块内，Npgsql 默认自动提交可用）；
/// 2) 执行内嵌 postgresql/schema.sql（CREATE TABLE/INDEX IF NOT EXISTS 天然幂等，按 ";" 分批；
///    脚本随引擎包内嵌）。脚本约定：不在字符串与注释中出现分号（scripts/README 已注明）。
/// </summary>
public partial class PostgreSqlSchemaInitializer(string connectionString, ILogger? logger = null) : ISchemaInitializer
{
    /// <summary>内嵌 schema 脚本资源名（csproj LogicalName，供单测断言）。</summary>
    public const string SchemaResourceName = "DBPilot.PostgreSql.Scripts.schema.sql";

    public void Initialize(bool createDatabaseIfMissing = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var dbName = string.IsNullOrWhiteSpace(builder.Database) ? "dbpilot" : builder.Database;

        if (createDatabaseIfMissing)
        {
            // 库名来自连接串配置，非用户输入；标识符再校验一次防注入
            if (!IdentifierRegex().IsMatch(dbName))
                throw new InvalidOperationException($"非法数据库名：{dbName}");

            builder.Database = "postgres";   // 目标库可能不存在，先连维护库
            using (var conn = new NpgsqlConnection(builder.ConnectionString))
            {
                conn.Open();
                using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @dbName", conn))
                {
                    check.Parameters.AddWithValue("dbName", dbName);
                    var exists = check.ExecuteScalar() is not null;
                    if (!exists)
                    {
                        using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
                        create.ExecuteNonQuery();
                    }
                }
            }
            logger?.LogInformation("平台库 {DbName} 已就绪（不存在时自动创建）", dbName);
        }

        var batches = SplitBatches(ReadEmbeddedSchema());
        logger?.LogInformation("平台库结构初始化（postgresql）：共 {Count} 个批次", batches.Length);

        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();
            foreach (var batch in batches)
            {
                using var cmd = new NpgsqlCommand(batch, conn) { CommandTimeout = 120 };
                cmd.ExecuteNonQuery();
            }
        }

        logger?.LogInformation("平台库结构初始化完成");
    }

    /// <summary>按 ";" 切分批次（剔除仅注释/空白的批次）；依赖脚本约定：字符串与注释中不含分号。</summary>
    public static string[] SplitBatches(string script) =>
        script.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !IsCommentOnly(b))
            .ToArray();

    private static bool IsCommentOnly(string batch)
    {
        // 批次去掉逐行 "--" 注释与空白后无内容视为注释批次
        var lines = batch.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("--"));
        return !lines.Any();
    }

    /// <summary>读取本引擎包内嵌的 schema 脚本。</summary>
    public static string ReadEmbeddedSchema()
    {
        using var stream = typeof(PostgreSqlSchemaInitializer).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {SchemaResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex IdentifierRegex();
}
