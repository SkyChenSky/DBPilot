using System.Text.RegularExpressions;
using DBPilot.Storage.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DBPilot.SqlServer;

/// <summary>
/// SQL Server 平台库初始化：
/// 1)（可选）库不存在时先连 master 建库；
/// 2) 执行内嵌 sqlserver/schema.sql（幂等，GO 分批，2008 兼容；脚本随引擎包内嵌）。
/// </summary>
public partial class SqlServerSchemaInitializer(string connectionString, ILogger? logger = null) : ISchemaInitializer
{
    /// <summary>内嵌 schema 脚本资源名（csproj LogicalName，供单测断言）。</summary>
    public const string SchemaResourceName = "DBPilot.SqlServer.Scripts.schema.sql";

    public void Initialize(bool createDatabaseIfMissing = true)
    {
        if (createDatabaseIfMissing)
            CreateDatabaseIfMissing();

        var batches = SplitBatches(ReadEmbeddedSchema());
        logger?.LogInformation("平台库结构初始化（sqlserver）：共 {Count} 个批次", batches.Length);

        using var conn = new SqlConnection(connectionString);
        conn.Open();
        foreach (var batch in batches)
        {
            using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }

        SchemaMigrations.Run(conn, typeof(SqlServerSchemaInitializer).Assembly,
            "DBPilot.SqlServer.Scripts.migrations.", VersionTableSql,
            _ => false,   // 脚本自带 IF NOT EXISTS 守卫
            logger);
        logger?.LogInformation("平台库结构初始化完成");
    }


    /// <summary>迁移历史表建表 SQL（internal 供单测锁方言坑：SQL Server 的 DATETIME 不支持精度参数，
    /// 须用 DATETIME2(3)——实测 0.5.5 首版在此写 DATETIME(3) 报 2716，主 schema 同款列即 DATETIME2(3)）。</summary>
    internal const string VersionTableSql = """
        IF OBJECT_ID(N'dbpilot_schema_version') IS NULL
            CREATE TABLE dbpilot_schema_version (
                version     NVARCHAR(64)  NOT NULL PRIMARY KEY,
                applied_at  DATETIME2(3)  NOT NULL CONSTRAINT df_dsv_applied DEFAULT (GETUTCDATE())
            )
        """;

    /// <summary>按 GO 行切分为独立批次（大小写不敏感，允许行首行尾空白）。</summary>
    public static string[] SplitBatches(string script) =>
        GoRegex().Split(script)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToArray();

    private void CreateDatabaseIfMissing()
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var dbName = string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? "dbpilot"
            : builder.InitialCatalog;

        builder.InitialCatalog = "master";
        using var conn = new SqlConnection(builder.ConnectionString);
        conn.Open();

        using var check = new SqlCommand(
            "SELECT COUNT(1) FROM sys.databases WHERE name = @name", conn);
        check.Parameters.AddWithValue("@name", dbName);
        var exists = (int)check.ExecuteScalar()! > 0;
        if (exists)
            return;

        // 库名来自连接串配置，非用户输入；标识符再校验一次防注入
        if (!IdentifierRegex().IsMatch(dbName))
            throw new InvalidOperationException($"非法数据库名：{dbName}");

        logger?.LogInformation("平台库 {DbName} 不存在，自动创建", dbName);
        using var create = new SqlCommand(
            $"CREATE DATABASE [{dbName}]", conn);
        create.ExecuteNonQuery();
    }

    /// <summary>读取本引擎包内嵌的 schema 脚本。</summary>
    public static string ReadEmbeddedSchema()
    {
        using var stream = typeof(SqlServerSchemaInitializer).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {SchemaResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
