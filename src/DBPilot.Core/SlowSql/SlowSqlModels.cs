namespace DBPilot.Core.SlowSql;

/// <summary>
/// 慢SQL增量游标（引擎无关载体）：SQL Server 走 fn_xe 的 file_name + file_offset（与死锁采集同形，
/// 重启丢失则全量重扫 + 判重兜底）；MySQL 走 mysql.slow_log 的 start_time 水位（µs 精度字符串，
/// 首次/重启无水位 = 全量重扫，与 XE 通道"清库后可重建历史"语义一致）。
/// </summary>
public class SlowSqlCursor
{
    public string? FileName { get; set; }
    public long? Offset { get; set; }

    /// <summary>MySQL slow_log start_time 水位（yyyy-MM-dd HH:mm:ss.ffffff，服务器本地时钟）。</summary>
    public string? Watermark { get; set; }
}

/// <summary>慢SQL增量读取结果。</summary>
public class SlowSqlReadResult
{
    /// <summary>XE 事件行（SQL Server 通道，EventData 为事件 XML，由 Core 统一解析）。</summary>
    public List<SlowSqlEventRow> Events { get; set; } = [];

    /// <summary>引擎侧已解析记录（MySQL slow_log 通道）；非空时 Core 跳过 XE 解析。</summary>
    public List<SlowSqlRecord>? Records { get; set; }

    public SlowSqlCursor? NewCursor { get; set; }
}

/// <summary>fn_xe 事件行（Chloe 按属性名精确映射，列别名须对齐）。</summary>
public class SlowSqlEventRow
{
    public string EventData { get; set; } = string.Empty;
}

/// <summary>XE 慢SQL事件解析结果。</summary>
public class SlowSqlRecord
{
    /// <summary>事件时刻（XE timestamp，实例时钟的 UTC；采集侧再按 clock_skew_seconds 校正）。</summary>
    public DateTime EventTimeUtc { get; set; }

    public string? DbName { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? AppName { get; set; }
    public int? SessionId { get; set; }

    /// <summary>1=rpc 2=batch</summary>
    public int SqlType { get; set; }

    public long DurationMs { get; set; }
    public long? CpuMs { get; set; }
    public long? LogicalReads { get; set; }
    public long? PhysicalReads { get; set; }
    public long? Writes { get; set; }
    public long? RowCount { get; set; }

    /// <summary>文本归一化指纹（SHA256 前 16 hex）。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>SQL 原文：rpc 取语句（collect_statement=1）、batch 取整批；超 64KB 截断。</summary>
    public string SqlText { get; set; } = string.Empty;
}
