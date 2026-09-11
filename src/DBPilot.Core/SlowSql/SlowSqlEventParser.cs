using System.Text.RegularExpressions;
using System.Xml.Linq;
using DBPilot.Common;
using DBPilot.Core.Deadlocks;

namespace DBPilot.Core.SlowSql;

/// <summary>
/// 慢SQL事件解析（平台侧纯函数）：
/// rpc_completed / sql_batch_completed 事件 XML → <see cref="SlowSqlRecord"/>。
/// duration 单位版本差异（2008 µs / 2012+ 实测口径）由调用方传 <paramref name="durationIsMicroseconds"/>。
/// 刻意只采这两类事件（不用 sql_statement_completed，避免与 rpc/batch 双计数）。
/// </summary>
public static partial class SlowSqlEventParser
{
    /// <summary>SQL 文本截断（与表列 NVARCHAR(MAX) 无关，防超长文本放大指纹/存储开销；64KB 口径）。
    /// 公开供 MySQL 通道复用同一截断口径。</summary>
    public const int MaxTextLength = 64 * 1024;

    public static SlowSqlRecord? ParseEvent(string eventXml, bool durationIsMicroseconds)
    {
        var root = XElement.Parse(eventXml);
        var name = (string?)root.Attribute("name");
        if (name is not ("rpc_completed" or "sql_batch_completed")) return null;

        var timeUtc = DeadlockReportParser.ParseTimestamp((string?)root.Attribute("timestamp"))
                      ?? throw new FormatException("XE event 缺少 timestamp 属性");

        // data 取值：fn_xe 文件形态 value 为转义文本；statement/batch_text 不会内嵌 XML，Value 即可
        string? Data(string n) => root.Elements("data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value;
        string? Action(string n) => root.Elements("action")
            .FirstOrDefault(a => (string?)a.Attribute("name") == n)?.Element("value")?.Value;
        long? DataLong(string n) => long.TryParse(Data(n), out var v) ? v : null;

        var text = (name == "rpc_completed" ? Data("statement") : Data("batch_text"))?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var rawDuration = DataLong("duration") ?? 0;
        var rawCpu = DataLong("cpu_time");
        return new SlowSqlRecord
        {
            EventTimeUtc = timeUtc,
            DbName = NullIfEmpty(Action("database_name")),
            LoginName = NullIfEmpty(Action("username")),
            HostName = NullIfEmpty(Action("client_hostname")),
            AppName = NullIfEmpty(Action("client_app_name")),
            SessionId = int.TryParse(Action("session_id"), out var sid) ? sid : null,
            SqlType = name == "rpc_completed" ? 1 : 2,
            DurationMs = durationIsMicroseconds ? rawDuration / 1000 : rawDuration,
            CpuMs = rawCpu is { } cpu ? (durationIsMicroseconds ? cpu / 1000 : cpu) : null,
            LogicalReads = DataLong("logical_reads"),
            PhysicalReads = DataLong("physical_reads"),
            Writes = DataLong("writes"),
            RowCount = DataLong("row_count"),
            Fingerprint = FingerprintOf(text),
            SqlText = text.Length > MaxTextLength ? text[..MaxTextLength] : text,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------- 文本归一化指纹（XE 通道无 query_hash，文本归一化；降级差值通道才有 query_hash） ----------

    /// <summary>指纹 = SHA256(归一化文本) 前 16 hex。归一化：参数字面量→?、空白折叠、小写，截前 4000 字符。</summary>
    public static string FingerprintOf(string sql)
        => Normalize(sql).Sha256PrefixHex();

    /// <summary>参数字面量替换：字符串（N'..' 含 '' 转义）→ 16 进制 → 数字（标识符/变量名中的数字不动）。</summary>
    public static string Normalize(string sql)
    {
        var s = sql.Trim();
        if (s.Length > 4000) s = s[..4000];
        s = StringLiteralRegex().Replace(s, "?");
        s = HexLiteralRegex().Replace(s, "?");
        s = NumberLiteralRegex().Replace(s, "?");
        return s.NormalizeWhitespace().ToLowerInvariant();
    }

    [GeneratedRegex(@"(?i)n?'(?:[^']|'')*'")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"(?i)(?<![0-9a-z_@$#])0x[0-9a-f]+(?![0-9a-z_])")]
    private static partial Regex HexLiteralRegex();

    /// <summary>数字字面量：前后无标识符字符（避免 col1 / @p2 / a1_b 中的数字被误替换）。</summary>
    [GeneratedRegex(@"(?<![0-9a-z_@$#])[+-]?\d+(?:\.\d+)?(?![0-9a-z_])")]
    private static partial Regex NumberLiteralRegex();
}
