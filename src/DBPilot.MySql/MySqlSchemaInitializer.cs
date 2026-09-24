using System.Text.RegularExpressions;
using DBPilot.Storage.Schema;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DBPilot.MySql;

/// <summary>
/// MySQL 平台库初始化：
/// 1)（可选）CREATE DATABASE IF NOT EXISTS（去 Database 直连执行）；
/// 2) 执行内嵌 mysql/schema.sql（CREATE TABLE IF NOT EXISTS 天然幂等，按 ";" 分批；脚本随引擎包内嵌）。
/// 脚本约定：不在字符串与注释中出现分号（scripts/README 已注明）。
/// </summary>
public partial class MySqlSchemaInitializer(string connectionString, ILogger? logger = null) : ISchemaInitializer
{
    /// <summary>内嵌 schema 脚本资源名（csproj LogicalName，供单测断言）。</summary>
    public const string SchemaResourceName = "DBPilot.MySql.Scripts.schema.sql";

    public void Initialize(bool createDatabaseIfMissing = true)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var dbName = string.IsNullOrWhiteSpace(builder.Database) ? "dbpilot" : builder.Database;

        if (createDatabaseIfMissing)
        {
            // 库名来自连接串配置，非用户输入；标识符再校验一次防注入
            if (!IdentifierRegex().IsMatch(dbName))
                throw new InvalidOperationException($"非法数据库名：{dbName}");

            builder.Database = string.Empty;   // 目标库可能不存在，先无库连接
            using (var conn = new MySqlConnection(builder.ConnectionString))
            {
                conn.Open();
                using var create = new MySqlCommand(
                    $"CREATE DATABASE IF NOT EXISTS `{dbName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci", conn);
                create.ExecuteNonQuery();
            }
            logger?.LogInformation("平台库 {DbName} 已就绪（不存在时自动创建）", dbName);
        }

        var batches = SplitBatches(ReadEmbeddedSchema());
        logger?.LogInformation("平台库结构初始化（mysql）：共 {Count} 个批次", batches.Length);

        using (var conn = new MySqlConnection(connectionString))
        {
            conn.Open();
            foreach (var batch in batches)
            {
                using var cmd = new MySqlCommand(batch, conn) { CommandTimeout = 120 };
                cmd.ExecuteNonQuery();
            }

            SchemaMigrations.Run(conn, typeof(MySqlSchemaInitializer).Assembly,
                "DBPilot.MySql.Scripts.migrations.",
                "CREATE TABLE IF NOT EXISTS dbpilot_schema_version (version VARCHAR(64) NOT NULL PRIMARY KEY, applied_at DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)))",
                ex => ex is MySqlException { Number: 1060 },   // MySQL 无 ADD COLUMN IF NOT EXISTS：重复列=已应用
                logger);
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
        using var stream = typeof(MySqlSchemaInitializer).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {SchemaResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex IdentifierRegex();
}
