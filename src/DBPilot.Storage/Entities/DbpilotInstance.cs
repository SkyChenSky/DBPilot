using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>接入的被监控实例（dbpilot_instance；engine 区分 SQL Server / MySQL）。</summary>
[Table("dbpilot_instance")]
public class DbpilotInstance
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("name")] public string Name { get; set; } = string.Empty;
    [Column("host")] public string Host { get; set; } = string.Empty;
    [Column("port")] public int Port { get; set; }

    /// <summary>引擎标识（小写：sqlserver / mysql），ProviderRegistry 路由键</summary>
    [Column("engine")] public string Engine { get; set; } = "sqlserver";

    [Column("login_name")] public string LoginName { get; set; } = string.Empty;
    [Column("password_cipher")] public string PasswordCipher { get; set; } = string.Empty;
    [Column("enabled")] public bool Enabled { get; set; }

    /// <summary>0未知 1在线 2退避 3离线</summary>
    [Column("status")] public int Status { get; set; }

    [Column("server_version")] public string? ServerVersion { get; set; }

    /// <summary>10=2008, 10.5=2008R2（以 105 表示）, 11=2012, ...</summary>
    [Column("major_version")] public int? MajorVersion { get; set; }

    [Column("edition")] public string? Edition { get; set; }
    [Column("cpu_cores")] public int? CpuCores { get; set; }
    [Column("machine_name")] public string? MachineName { get; set; }

    /// <summary>tempdb create_date，用于实例重启检测</summary>
    [Column("sqlserver_start_time")] public DateTime? SqlServerStartTime { get; set; }

    [Column("clock_skew_seconds")] public int ClockSkewSeconds { get; set; }
    [Column("xe_file_path")] public string? XeFilePath { get; set; }
    [Column("slow_sql_threshold_ms")] public int SlowSqlThresholdMs { get; set; }
    [Column("blocking_threshold_sec")] public int BlockingThresholdSec { get; set; }
    [Column("env_tag")] public string? EnvTag { get; set; }

    /// <summary>JSON：库白/黑名单</summary>
    [Column("db_filter")] public string? DbFilter { get; set; }

    [Column("last_error")] public string? LastError { get; set; }
    [Column("last_heartbeat")] public DateTime? LastHeartbeat { get; set; }
    [Column("create_time")] public DateTime CreateTime { get; set; }
    [Column("update_time")] public DateTime? UpdateTime { get; set; }
}
