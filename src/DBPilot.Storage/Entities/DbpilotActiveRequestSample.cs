using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>活动会话分钟聚合样本（dbpilot_active_request_sample；10s 原始样本仅内存）。</summary>
[Table("dbpilot_active_request_sample")]
public class DbpilotActiveRequestSample
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    [Column("instance_id")] public int InstanceId { get; set; }

    /// <summary>整分（UTC）</summary>
    [Column("minute_time")] public DateTime MinuteTime { get; set; }

    [Column("sample_count")] public int SampleCount { get; set; }
    [Column("avg_active_sessions")] public decimal AvgActiveSessions { get; set; }
    [Column("max_active_sessions")] public int MaxActiveSessions { get; set; }

    /// <summary>{"cpu":0.21,"userIo":0.03,...} 等待分桶 JSON</summary>
    [Column("buckets")] public string Buckets { get; set; } = string.Empty;

    /// <summary>7 维度 TopN JSON</summary>
    [Column("dims")] public string Dims { get; set; } = string.Empty;

    [Column("create_time")] public DateTime CreateTime { get; set; }
}
