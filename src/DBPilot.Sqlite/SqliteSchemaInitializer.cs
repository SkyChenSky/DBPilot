using DBPilot.Storage.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DBPilot.Sqlite;

/// <summary>
/// SQLite 平台库初始化：打开连接（文件不存在即自动创建，无建库段）后
/// 执行内嵌 sqlite/schema.sql（CREATE TABLE/INDEX IF NOT EXISTS 天然幂等，按 ";" 分批；脚本随引擎包内嵌）。
/// WAL 模式由 DBPilotSqliteContext 的连接工厂逐连接设置（库级持久，幂等）。
/// 脚本约定：不在字符串与注释中出现分号（scripts/README 已注明）。
/// </summary>
public partial class SqliteSchemaInitializer(string connectionString, ILogger? logger = null) : ISchemaInitializer
{
    /// <summary>内嵌 schema 脚本资源名（csproj LogicalName，供单测断言）。</summary>
    public const string SchemaResourceName = "DBPilot.Sqlite.Scripts.schema.sql";

    public void Initialize(bool createDatabaseIfMissing = true)
    {
        // createDatabaseIfMissing 对 SQLite 无意义（打开连接即建库文件），参数保留对齐接口形态

        var batches = SplitBatches(ReadEmbeddedSchema());
        logger?.LogInformation("平台库结构初始化（sqlite）：共 {Count} 个批次", batches.Length);

        using var conn = DBPilotSqliteContext.CreateConfiguredConnection(connectionString);
        foreach (var batch in batches)
        {
            using var cmd = new SqliteCommand(batch, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }

        SchemaMigrations.Run(conn, typeof(SqliteSchemaInitializer).Assembly,
            "DBPilot.Sqlite.Scripts.migrations.",
            "CREATE TABLE IF NOT EXISTS dbpilot_schema_version (version TEXT NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL DEFAULT (datetime('now')))",
            ex => ex is SqliteException e && e.Message.Contains("duplicate column name"),   // SQLite 无列守卫：重复列=已应用
            logger);
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
        using var stream = typeof(SqliteSchemaInitializer).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {SchemaResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
