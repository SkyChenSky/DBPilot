using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>磁盘卷用量快照（dbpilot_instance_disk，每卷一行；2008 无 dm_os_volume_stats 不落）。</summary>
[Table("dbpilot_instance_disk")]
public class DbpilotInstanceDisk
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public long Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }

    [Column("volume_mount_point")] public string VolumeMountPoint { get; set; } = string.Empty;

    [Column("total_mb")] public long? TotalMb { get; set; }
    [Column("available_mb")] public long? AvailableMb { get; set; }

    /// <summary>使用率 %（total ≤ 0 容错 null）。</summary>
    [Column("used_pct")] public decimal? UsedPct { get; set; }

    [Column("sample_time")] public DateTime SampleTime { get; set; }
}
