using System.Reflection;
using Chloe.Annotations;
using DBPilot.MySql;
using DBPilot.PostgreSql;
using DBPilot.SqlServer;
using DBPilot.Sqlite;
using DBPilot.Storage;
using DBPilot.Storage.Entities;

namespace DBPilot.UnitTests;

/// <summary>
/// 平台库 schema 四方言校验：SQL Server（GO 分批 + OBJECT_ID 幂等）、
/// MySQL（";" 分批 + CREATE TABLE IF NOT EXISTS 幂等，索引内联）、
/// SQLite（";" 分批 + CREATE TABLE/INDEX IF NOT EXISTS 幂等，索引独立语句）与
/// PostgreSQL（";" 分批 + CREATE TABLE/INDEX IF NOT EXISTS 幂等，索引独立语句）逐表逐列与实体对齐。
/// </summary>
public class SchemaTests
{
    private static readonly string[] ExpectedTables =
    [
        "dbpilot_instance",
        "dbpilot_sql_template",
        "dbpilot_active_request_sample",
        "dbpilot_top_sql_delta",
        "dbpilot_slow_sql",
        "dbpilot_deadlock_event",
        "dbpilot_deadlock_process",
        "dbpilot_deadlock_resource",
        "dbpilot_blocking_event",
        "dbpilot_missing_index_snapshot",
        "dbpilot_index_usage_snapshot",
        "dbpilot_top_sql_exclusion",
        "dbpilot_query_plan",
        "dbpilot_plan_change",
        "dbpilot_instance_metrics",
        "dbpilot_instance_disk",
    ];

    public static TheoryData<string> Dialects => new()
    {
        SqlServerSchemaInitializer.SchemaResourceName,
        MySqlSchemaInitializer.SchemaResourceName,
        SqliteSchemaInitializer.SchemaResourceName,
        PostgreSqlSchemaInitializer.SchemaResourceName,
    };

    private static string ReadSchemaSql(string resourceName) => resourceName switch
    {
        SqlServerSchemaInitializer.SchemaResourceName => SqlServerSchemaInitializer.ReadEmbeddedSchema(),
        MySqlSchemaInitializer.SchemaResourceName => MySqlSchemaInitializer.ReadEmbeddedSchema(),
        SqliteSchemaInitializer.SchemaResourceName => SqliteSchemaInitializer.ReadEmbeddedSchema(),
        PostgreSqlSchemaInitializer.SchemaResourceName => PostgreSqlSchemaInitializer.ReadEmbeddedSchema(),
        _ => throw new ArgumentOutOfRangeException(nameof(resourceName)),
    };

    [Fact]
    public void SplitBatches_SqlServer_按GO切分_去除空批次()
    {
        var batches = SqlServerSchemaInitializer.SplitBatches("SELECT 1\nGO\nGO\nSELECT 2\ngo\n-- comment only\nGO\n");
        Assert.Equal(3, batches.Length);
        Assert.Equal("SELECT 1", batches[0]);
        Assert.Equal("SELECT 2", batches[1]);
        Assert.Equal("-- comment only", batches[2]);
    }

    [Fact]
    public void SplitBatches_MySql_按分号切分_去除空与纯注释批次()
    {
        var script = """
            -- 建表
            CREATE TABLE a (id INT);
            ;;
            -- 仅注释
            CREATE TABLE b (id INT)
            """;
        var batches = MySqlSchemaInitializer.SplitBatches(script);
        Assert.Equal(2, batches.Length);
        Assert.Contains("CREATE TABLE a", batches[0]);
        Assert.Contains("CREATE TABLE b", batches[1]);
        Assert.DoesNotContain("CREATE TABLE b", batches[0]);
    }

    [Fact]
    public void SplitBatches_Sqlite_与MySql同款切分逻辑()
    {
        // SQLite 与 MySQL 共用 ";" 切批约定（同一实现形态），行为一致性锚定
        var batches = SqliteSchemaInitializer.SplitBatches("CREATE TABLE a (id INT);\n-- 仅注释\n");
        Assert.Single(batches);
        Assert.Contains("CREATE TABLE a", batches[0]);
    }

    [Fact]
    public void SplitBatches_PostgreSql_与MySql同款切分逻辑()
    {
        // PostgreSQL 与 MySQL/SQLite 共用 ";" 切批约定（同一实现形态），行为一致性锚定
        var batches = PostgreSqlSchemaInitializer.SplitBatches("CREATE TABLE a (id INT);\n-- 仅注释\n");
        Assert.Single(batches);
        Assert.Contains("CREATE TABLE a", batches[0]);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SchemaSql_包含全部十六张表(string resource)
    {
        var sql = ReadSchemaSql(resource);
        foreach (var table in ExpectedTables)
        {
            var marker = resource == SqlServerSchemaInitializer.SchemaResourceName
                ? $"OBJECT_ID(N'{table}')"
                : $"CREATE TABLE IF NOT EXISTS {table} (";
            Assert.Contains(marker, sql);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SchemaSql_MySql不含分号禁区的字符串默认值与注释(string resource)
    {
        if (resource != MySqlSchemaInitializer.SchemaResourceName)
            return;   // 约定仅约束 MySQL 脚本（按 ";" 分批）

        var sql = ReadSchemaSql(resource);
        var batches = MySqlSchemaInitializer.SplitBatches(sql);
        // 批次数 = 表数（全部为建表语句，索引已内联；首批允许携带头注释）
        Assert.Equal(ExpectedTables.Length, batches.Length);
        foreach (var batch in batches)
            Assert.Contains("CREATE TABLE", batch);
    }

    [Fact]
    public void SchemaSql_Sqlite_批次数_等于表加索引数()
    {
        var sql = ReadSchemaSql(SqliteSchemaInitializer.SchemaResourceName);
        var batches = SqliteSchemaInitializer.SplitBatches(sql);
        var indexCount = sql.Split('\n')
            .Count(l => l.TrimStart().StartsWith("CREATE INDEX IF NOT EXISTS")
                     || l.TrimStart().StartsWith("CREATE UNIQUE INDEX IF NOT EXISTS"));
        // SQLite 索引不内联（CREATE TABLE 不支持），独立幂等语句；全部批次均为 DDL
        Assert.Equal(ExpectedTables.Length + indexCount, batches.Length);
        foreach (var batch in batches)
            Assert.True(batch.Contains("CREATE TABLE") || batch.Contains("INDEX"), $"非 DDL 批次：{batch}");
    }

    [Fact]
    public void SchemaSql_PostgreSql_批次数_等于表加索引数()
    {
        var sql = ReadSchemaSql(PostgreSqlSchemaInitializer.SchemaResourceName);
        var batches = PostgreSqlSchemaInitializer.SplitBatches(sql);
        var indexCount = sql.Split('\n')
            .Count(l => l.TrimStart().StartsWith("CREATE INDEX IF NOT EXISTS")
                     || l.TrimStart().StartsWith("CREATE UNIQUE INDEX IF NOT EXISTS"));
        // PG 索引独立幂等语句（与 SQLite 同形态）；全部批次均为 DDL，表名/列名全小写免引号
        Assert.Equal(ExpectedTables.Length + indexCount, batches.Length);
        Assert.Equal(17, indexCount);
        foreach (var batch in batches)
            Assert.True(batch.Contains("CREATE TABLE") || batch.Contains("INDEX"), $"非 DDL 批次：{batch}");
    }

    [Fact]
    public void SchemaSql_PostgreSql_类型与自增口径_PG原生形态()
    {
        var sql = ReadSchemaSql(PostgreSqlSchemaInitializer.SchemaResourceName);
        // 自增：GENERATED BY DEFAULT AS IDENTITY（Chloe 插列不指 id，两形态均兼容）——按完整主键短语计数免头注释误计
        Assert.Equal(16, sql.Split("GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY").Length - 1);
        // 时间列 timestamptz（即时值语义，Npgsql 往返即 UTC）；不出现 MySQL 墙钟口径
        Assert.DoesNotContain("UTC_TIMESTAMP", sql);
        Assert.DoesNotContain("DATETIME", sql);
        Assert.DoesNotContain("TIMESTAMP WITHOUT TIME ZONE", sql);
        // 布尔口径：TINYINT(1) → BOOLEAN
        Assert.DoesNotContain("TINYINT", sql);
        Assert.Contains("BOOLEAN", sql);
        // 无 MySQL 表选项
        Assert.DoesNotContain("ENGINE=InnoDB", sql);
        Assert.DoesNotContain("AUTO_INCREMENT", sql);
    }

    [Fact]
    public void 实体_Table注解_与schema表名一一对应()
    {
        var entityAssembly = typeof(DbpilotInstance).Assembly;
        var tableNames = entityAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TableAttribute>() is not null)
            .Select(t => t.GetCustomAttribute<TableAttribute>()!.Name)
            .ToHashSet();

        Assert.Equal(ExpectedTables.Length, tableNames.Count);
        foreach (var table in ExpectedTables)
            Assert.Contains(table, tableNames);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void 实体_Column注解_列名全部存在于对应表DDL(string resource)
    {
        var sql = ReadSchemaSql(resource);
        var entityAssembly = typeof(DbpilotInstance).Assembly;

        foreach (var type in entityAssembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<TableAttribute>() is not null))
        {
            var table = type.GetCustomAttribute<TableAttribute>()!.Name;
            // 截取该表的 CREATE TABLE 段落（双方言统一：段落以行首 ")" 结束）
            var start = sql.IndexOf($"CREATE TABLE {table} (", StringComparison.Ordinal)
                .Let(s => s >= 0 ? s : sql.IndexOf($"CREATE TABLE IF NOT EXISTS {table} (", StringComparison.Ordinal));
            Assert.True(start >= 0, $"{table} 未在 schema.sql 中定义");

            var end = sql.IndexOf("\n)", start, StringComparison.Ordinal);
            var ddl = sql[start..end];

            foreach (var prop in type.GetProperties())
            {
                if (prop.GetCustomAttribute<NotMappedAttribute>() != null) continue;   // 非映射属性（采集期寻址键等）
                var col = prop.GetCustomAttribute<ColumnAttribute>()
                    ?? throw new InvalidOperationException($"{type.Name}.{prop.Name} 缺少 Column 注解");
                Assert.True(
                    ddl.Contains($" {col.Name.ToLower()} ") || ddl.Contains($"\n    {col.Name.ToLower()} "),
                    $"{table} 缺少列 {col.Name}（实体 {type.Name}.{prop.Name}）");
            }
        }
    }

    [Fact]
    public void 实体_主键与自增_均配置在Id列()
    {
        var entityAssembly = typeof(DbpilotInstance).Assembly;
        foreach (var type in entityAssembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<TableAttribute>() is not null))
        {
            var idProp = type.GetProperty("Id");
            Assert.NotNull(idProp);
            Assert.True(idProp!.GetCustomAttribute<ColumnAttribute>()!.IsPrimaryKey, $"{type.Name}.Id 应为主键");
            Assert.NotNull(idProp.GetCustomAttribute<AutoIncrementAttribute>());
        }
    }

    [Theory]
    [InlineData("sqlserver", typeof(SqlServerSchemaInitializer))]
    [InlineData("mysql", typeof(MySqlSchemaInitializer))]
    [InlineData("sqlite", typeof(SqliteSchemaInitializer))]
    [InlineData("postgresql", typeof(PostgreSqlSchemaInitializer))]
    public void 存储模块_按引擎创建初始化器(string engine, Type expected)
    {
        var module = new PlatformStorageRegistry([new SqlServerStorageModule(), new MySqlStorageModule(), new SqliteStorageModule(), new PostgreSqlStorageModule()])
            .Resolve(engine);
        Assert.IsType(expected, module.CreateInitializer("Server=.;Database=dbpilot;"));
    }
}

file static class IntExtensions
{
    /// <summary>小工具：indexof 链式回退（-1 时取备选值）。</summary>
    public static int Let(this int value, Func<int, int> fallback) => value >= 0 ? value : fallback(value);
}
