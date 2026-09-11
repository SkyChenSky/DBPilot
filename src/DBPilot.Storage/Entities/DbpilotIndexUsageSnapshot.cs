using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>索引使用率快照（dbpilot_index_usage_snapshot）。</summary>
[Table("dbpilot_index_usage_snapshot")]
public class DbpilotIndexUsageSnapshot
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }
    [Column("db_name")] public string DbName { get; set; } = string.Empty;
    [Column("table_name")] public string TableName { get; set; } = string.Empty;
    [Column("index_name")] public string IndexName { get; set; } = string.Empty;
    [Column("is_primary_key")] public bool IsPrimaryKey { get; set; }
    [Column("is_unique")] public bool IsUnique { get; set; }
    [Column("type_desc")] public string TypeDesc { get; set; } = string.Empty;
    [Column("user_seeks")] public long UserSeeks { get; set; }
    [Column("user_scans")] public long UserScans { get; set; }
    [Column("user_lookups")] public long UserLookups { get; set; }
    [Column("user_updates")] public long UserUpdates { get; set; }
    [Column("last_user_seek")] public DateTime? LastUserSeek { get; set; }
    [Column("last_user_scan")] public DateTime? LastUserScan { get; set; }
    [Column("key_columns")] public string? KeyColumns { get; set; }
    [Column("used_page_count")] public long? UsedPageCount { get; set; }
    [Column("last_user_update")] public DateTime? LastUserUpdate { get; set; }
    [Column("avg_fragmentation_percent")] public double? AvgFragmentationPercent { get; set; }
    [Column("is_unused")] public bool IsUnused { get; set; }
    [Column("snapshot_time")] public DateTime SnapshotTime { get; set; }
}
