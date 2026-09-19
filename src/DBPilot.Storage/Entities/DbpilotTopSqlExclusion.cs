using Chloe.Annotations;

namespace DBPilot.Storage.Entities;

/// <summary>
/// Top SQL 指纹黑名单（dbpilot_top_sql_exclusion，UNIQUE(fingerprint)）。
/// 全局生效：query_hash 对相同脚本是确定性稳定的，跨实例通用；页面“排除”按钮写入。
/// </summary>
[Table("dbpilot_top_sql_exclusion")]
public class DbpilotTopSqlExclusion
{
    [Column("id", IsPrimaryKey = true)]
    [AutoIncrement]
    public int Id { get; set; }

    /// <summary>query_hash hex（16 位）或 sql_handle hex 兜底（32 位）。</summary>
    [Column("fingerprint")] public string Fingerprint { get; set; } = string.Empty;

    /// <summary>语句开头片段（备注用，便于识别排除的是什么）。</summary>
    [Column("sql_head")] public string? SqlHead { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
