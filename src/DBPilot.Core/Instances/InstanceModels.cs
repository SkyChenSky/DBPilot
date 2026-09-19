using DBPilot.Core.Providers;
using DBPilot.Storage.Entities;

namespace DBPilot.Core.Instances;

/// <summary>
/// 解密后的实例运行时配置（内存中传递，Provider 消费；严禁写入日志/响应）。
/// </summary>
public class InstanceConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1433;

    /// <summary>引擎标识（dbpilot_instance.engine，RoutingDatabaseProvider 路由键；空 = 旧数据按 sqlserver）。</summary>
    public string Engine { get; set; } = DbpilotEngines.SqlServer;

    public string LoginName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>主版本（10=2008、105=2008R2、11=2012 … 17=2025；探测时落库，供 Provider 做 SQL 版本分支）。</summary>
    public int MajorVersion { get; set; }

    public int SlowSqlThresholdMs { get; set; }
    public int BlockingThresholdSec { get; set; }
    public string? XeFilePath { get; set; }
    public string? DbFilter { get; set; }
}

/// <summary>
/// 创建/更新实例请求。更新时 Password 为空表示保持原密码不变。
/// </summary>
public class InstanceSaveRequest
{
    public string? Name { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }

    /// <summary>引擎标识（sqlserver / mysql）；创建缺省 = sqlserver，更新为空 = 保持不变。</summary>
    public string? Engine { get; set; }

    public string? LoginName { get; set; }

    /// <summary>创建必填；更新时为空 = 不修改密码。</summary>
    public string? Password { get; set; }

    public bool Enabled { get; set; } = true;
    public string? EnvTag { get; set; }
    public int SlowSqlThresholdMs { get; set; } = 1000;
    public int BlockingThresholdSec { get; set; } = 5;
    public string? XeFilePath { get; set; }
    public string? DbFilter { get; set; }
}

/// <summary>
/// 实例列表项（对外输出，不含任何凭据）。
/// </summary>
public class InstanceListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }

    /// <summary>引擎标识（sqlserver / mysql）。</summary>
    public string Engine { get; set; } = "sqlserver";

    public string LoginName { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>0未知 1在线 2退避 3离线</summary>
    public int Status { get; set; }

    public string? ServerVersion { get; set; }
    public int? MajorVersion { get; set; }
    public string? Edition { get; set; }
    public int? CpuCores { get; set; }
    public string? MachineName { get; set; }
    public string? EnvTag { get; set; }
    public int SlowSqlThresholdMs { get; set; }
    public int BlockingThresholdSec { get; set; }
    public string? XeFilePath { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }

    public static InstanceListItem From(DbpilotInstance e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Host = e.Host,
        Port = e.Port,
        Engine = e.Engine,
        LoginName = e.LoginName,
        Enabled = e.Enabled,
        Status = e.Status,
        ServerVersion = e.ServerVersion,
        MajorVersion = e.MajorVersion,
        Edition = e.Edition,
        CpuCores = e.CpuCores,
        MachineName = e.MachineName,
        EnvTag = e.EnvTag,
        SlowSqlThresholdMs = e.SlowSqlThresholdMs,
        BlockingThresholdSec = e.BlockingThresholdSec,
        XeFilePath = e.XeFilePath,
        LastError = e.LastError,
        LastHeartbeat = e.LastHeartbeat,
        CreateTime = e.CreateTime,
        UpdateTime = e.UpdateTime
    };
}

/// <summary>
/// 版本探测结果（接入时一次，缓存 instance 表）。
/// </summary>
public class InstanceMeta
{
    public string ProductVersion { get; set; } = string.Empty;

    /// <summary>10=2008, 105=2008R2, 11=2012, 12=2014 …</summary>
    public int MajorVersion { get; set; }

    public string Edition { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public int CpuCores { get; set; }

    /// <summary>tempdb create_date（UTC），实例重启检测。</summary>
    public DateTime? SqlServerStartTimeUtc { get; set; }

    /// <summary>实例默认日志目录（SQL Server：SERVERPROPERTY('ErrorLogFileName') 剥文件名）——
    /// xe_file_path 留空时自动探测回填（慢SQL XE 会话文件目标目录）；MySQL 无此概念恒 null。</summary>
    public string? DefaultLogPath { get; set; }

    public int ClockSkewSeconds { get; set; }
}

/// <summary>
/// 连接测试 + 权限自检结果。
/// </summary>
public class ConnectionTestResult
{
    public bool Ok { get; set; }

    /// <summary>连通失败时的错误信息（如网络不可达/登录失败）。</summary>
    public string? Error { get; set; }

    /// <summary>连接耗时（毫秒）。</summary>
    public int LatencyMs { get; set; }

    public List<MissingPermission> MissingPermissions { get; set; } = [];
}

/// <summary>缺失的服务器级权限：影响说明 + 修复脚本。</summary>
public class MissingPermission
{
    public string Permission { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string FixScript { get; set; } = string.Empty;
}
