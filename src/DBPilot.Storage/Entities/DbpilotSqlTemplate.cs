using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>SQL 模板库（dbpilot_sql_template，UNIQUE(instance_id, fingerprint)）。</summary>
[Table("dbpilot_sql_template")]
public class DbpilotSqlTemplate
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }

    /// <summary>query_hash hex 或归一化文本哈希</summary>
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;

    [Column("sql_text")] public string SqlText { get; set; } = string.Empty;
    [Column("first_seen")] public DateTime FirstSeen { get; set; }
    [Column("last_seen")] public DateTime LastSeen { get; set; }
}
