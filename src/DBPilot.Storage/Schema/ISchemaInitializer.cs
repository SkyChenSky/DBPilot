namespace DBPilot.Storage.Schema;

/// <summary>
/// 平台库结构初始化策略：脚本内嵌于各引擎包（scripts/database/{engine}/schema.sql），幂等可重跑。
/// 实现随引擎包分发（引擎包重构：SqlServerSchemaInitializer / MySqlSchemaInitializer）。
/// </summary>
public interface ISchemaInitializer
{
    /// <summary>建库（如缺失，可选）并执行该方言的 schema.sql。</summary>
    void Initialize(bool createDatabaseIfMissing = true);
}
