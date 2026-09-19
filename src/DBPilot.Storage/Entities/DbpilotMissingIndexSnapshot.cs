using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>缺失索引诊断快照（dbpilot_missing_index_snapshot）。</summary>
[Table("dbpilot_missing_index_snapshot")]
public class DbpilotMissingIndexSnapshot
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("db_name")] public string DbName { get; set; } = string.Empty;
    [Column("table_name")] public string TableName { get; set; } = string.Empty;
    [Column("equality_columns")] public string? EqualityColumns { get; set; }
    [Column("inequality_columns")] public string? InequalityColumns { get; set; }
    [Column("included_columns")] public string? IncludedColumns { get; set; }
    [Column("user_seeks")] public long UserSeeks { get; set; }
    [Column("avg_total_user_cost")] public double AvgTotalUserCost { get; set; }
    [Column("avg_user_impact")] public double AvgUserImpact { get; set; }
    [Column("score")] public double Score { get; set; }
    [Column("last_user_seek")] public DateTime? LastUserSeek { get; set; }
    [Column("table_pages")] public int? TablePages { get; set; }
    [Column("table_rows")] public long? TableRows { get; set; }
    [Column("create_index_sql")] public string CreateIndexSql { get; set; } = string.Empty;
    [Column("snapshot_time")] public DateTime SnapshotTime { get; set; }
}
