using DBPilot.Storage.Dialect;

namespace DBPilot.Sqlite;

/// <summary>
/// SQLite 平台库方言（仅平台库轴，无监控 Provider）：以 MySqlDialect 为底本翻译——
/// DATEADD/DATEDIFF 桶→strftime 截断（时间列存 ISO8601 文本，返回 TEXT 且字典序=时间序，
/// 直接截断墙钟 UTC 值语义与 SQL Server 一致）、DELETE TOP→rowid 子查询 + LIMIT
/// （Microsoft.Data.Sqlite 编译不带 DELETE..LIMIT 开关）、@@ROWCOUNT→changes()、
/// N''→''（无 Unicode 前缀）、SUBSTRING→substr、CAST(.. AS SIGNED)→INTEGER。
/// ESCAPE '\'：SQLite 字符串字面量反斜杠不转义，单反斜杠即转义符（MySQL 须 '\\' 双写）。
/// LIMIT @skip, @take / 窗口函数 / 反引号标识符 SQLite 均原生兼容，照抄 MySQL 版。
/// </summary>
public sealed class SqliteDialect : IPlatformDialect
{
    /// <summary>DATEADD/DATEDIFF 桶的时间截断等价表达式（按 unit 选格式串，直接截断列的墙钟值）。</summary>
    private static string Bucket(string column, string unit) => unit switch
    {
        "minute" => $"strftime('%Y-%m-%d %H:%M:00', {column})",
        "hour"   => $"strftime('%Y-%m-%d %H:00:00', {column})",
        "day"    => $"strftime('%Y-%m-%d 00:00:00', {column})",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "未知桶粒度"),
    };

    public string DeadlockPageCountSql(string condSql) => $"""
        /* dbpilot */
        SELECT COUNT(*) FROM dbpilot_deadlock_event e
        WHERE e.instance_id = @instanceId AND e.event_time >= @start AND e.event_time < @end {condSql}
        """;

    public string DeadlockPageRowsSql(string condSql) => $"""
        /* dbpilot */
        SELECT e.id AS Id, e.event_time AS EventTime,
               e.victim_spids AS VictimSpids, e.fingerprint AS Fingerprint
        FROM dbpilot_deadlock_event e
        WHERE e.instance_id = @instanceId AND e.event_time >= @start AND e.event_time < @end {condSql}
        ORDER BY e.event_time DESC
        LIMIT @skip, @take
        """;

    public string DeadlockFilterLoginsSql() => """
        /* dbpilot */
        SELECT DISTINCT p.login_name FROM dbpilot_deadlock_process p
        WHERE p.instance_id = @instanceId AND p.event_time >= @start AND p.event_time < @end
          AND p.login_name IS NOT NULL
        ORDER BY p.login_name
        """;

    public string DeadlockFilterHostsSql() => """
        /* dbpilot */
        SELECT DISTINCT p.host_name FROM dbpilot_deadlock_process p
        WHERE p.instance_id = @instanceId AND p.event_time >= @start AND p.event_time < @end
          AND p.host_name IS NOT NULL
        ORDER BY p.host_name
        """;

    public string DeadlockTrendTotalsSql(string unit) => $"""
        /* dbpilot */
        SELECT {Bucket("event_time", unit)} AS TimeUtc, COUNT(*) AS Total
        FROM dbpilot_deadlock_event
        WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
        GROUP BY {Bucket("event_time", unit)}
        """;

    public string DeadlockTrendColorsSql(string unit) => $"""
        /* dbpilot */
        SELECT {Bucket("event_time", unit)} AS TimeUtc,
               COUNT(DISTINCT CASE WHEN resource_type = 'keylock'    THEN event_id END) AS KeyLocks,
               COUNT(DISTINCT CASE WHEN resource_type = 'objectlock' THEN event_id END) AS ObjectLocks,
               COUNT(DISTINCT CASE WHEN resource_type = 'pagelock'   THEN event_id END) AS PageLocks,
               COUNT(DISTINCT CASE WHEN resource_type = 'ridlock'    THEN event_id END) AS RidLocks,
               COUNT(DISTINCT CASE WHEN resource_type NOT IN ('keylock','objectlock','pagelock','ridlock')
                                   THEN event_id END) AS OtherLocks
        FROM dbpilot_deadlock_resource
        WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
        GROUP BY {Bucket("event_time", unit)}
        """;

    public string DeadlockFingerprintStatsSql() => """
        /* dbpilot */
        SELECT s.Fingerprint, s.Cnt AS `Count`, s.FirstTimeUtc, s.LastTimeUtc, e.id AS LastEventId
        FROM (SELECT fingerprint AS Fingerprint, COUNT(*) AS Cnt,
                     MIN(event_time) AS FirstTimeUtc, MAX(event_time) AS LastTimeUtc
              FROM dbpilot_deadlock_event
              WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
              GROUP BY fingerprint) s
        JOIN (SELECT id, fingerprint,
                     ROW_NUMBER() OVER (PARTITION BY fingerprint ORDER BY event_time DESC, id DESC) AS rn
              FROM dbpilot_deadlock_event
              WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end) e
          ON e.fingerprint = s.Fingerprint AND e.rn = 1
        ORDER BY s.Cnt DESC
        """;

    public string DeadlockObjectFilterSql() => """
        EXISTS (SELECT 1 FROM dbpilot_deadlock_resource r
                WHERE r.event_id = e.id AND r.object_name LIKE @objectName ESCAPE '\')
        """;

    public string PlanVersionsSql() => """
        /* dbpilot */
        SELECT id                   AS PlanId,
               query_plan_hash     AS QueryPlanHash,
               compile_time_utc    AS CompileTimeUtc,
               first_seen_utc      AS FirstSeenUtc,
               last_seen_utc       AS LastSeenUtc,
               execution_count     AS ExecutionCount,
               CASE WHEN execution_count = 0 THEN NULL ELSE total_elapsed_ms / execution_count END     AS AvgElapsedMs,
               CASE WHEN execution_count = 0 THEN NULL ELSE total_worker_ms / execution_count END      AS AvgWorkerMs,
               CASE WHEN execution_count = 0 THEN NULL ELSE total_logical_reads / execution_count END  AS AvgReads,
               CASE WHEN plan_xml IS NULL THEN 0 ELSE 1 END                                             AS HasXml
        FROM dbpilot_query_plan
        WHERE instance_id = @instanceId AND fingerprint = @fingerprint
          AND (@db = '' OR db_name = @db)
        ORDER BY last_seen_utc DESC
        """;

    public string PlanChangesTopSql() => """
        /* dbpilot */
        SELECT id AS Id, old_plan_hash AS OldPlanHash, new_plan_hash AS NewPlanHash,
               old_avg_elapsed_ms AS OldAvgElapsedMs, new_avg_elapsed_ms AS NewAvgElapsedMs,
               old_avg_worker_ms AS OldAvgWorkerMs, new_avg_worker_ms AS NewAvgWorkerMs,
               old_avg_reads AS OldAvgReads, new_avg_reads AS NewAvgReads,
               old_exec_count AS OldExecCount, new_exec_count AS NewExecCount,
               changed_at_utc AS ChangedAtUtc
        FROM dbpilot_plan_change
        WHERE instance_id = @instanceId AND fingerprint = @fingerprint
        ORDER BY changed_at_utc DESC
        LIMIT 200
        """;

    public string PlanChangeBoardSql(int n) => $"""
        /* dbpilot */
        SELECT c.id AS Id, c.instance_id AS InstanceId, c.fingerprint AS Fingerprint, c.db_name AS DbName,
               t.sql_text AS SqlText,
               c.old_plan_hash AS OldPlanHash, c.new_plan_hash AS NewPlanHash,
               c.old_avg_elapsed_ms AS OldAvgElapsedMs, c.new_avg_elapsed_ms AS NewAvgElapsedMs,
               c.old_avg_worker_ms AS OldAvgWorkerMs, c.new_avg_worker_ms AS NewAvgWorkerMs,
               c.old_avg_reads AS OldAvgReads, c.new_avg_reads AS NewAvgReads,
               c.old_exec_count AS OldExecCount, c.new_exec_count AS NewExecCount,
               c.changed_at_utc AS ChangedAtUtc
        FROM dbpilot_plan_change c
        LEFT JOIN dbpilot_sql_template t
             ON t.instance_id = c.instance_id AND t.fingerprint = c.fingerprint
        WHERE c.instance_id = @instanceId
        ORDER BY c.changed_at_utc DESC
        LIMIT {n}
        """;

    public string SlowSqlTemplatesSql(string where) => $"""
        /* dbpilot */
        SELECT fingerprint                                                     AS Fingerprint,
               substr(MAX(sql_text), 1, 500)                                   AS SampleSql,   -- 样例取字典序最大文本（同类模板文本相近）
               MAX(db_name)                                                    AS DbName,
               COUNT(*)                                                        AS Count,
               -- SQLite 无 DECIMAL 类型：CAST(...) AS DECIMAL 仅 NUMERIC 亲和性不舍入（浮点原样透传），小数收敛一律 ROUND
               ROUND(100.0 * SUM(duration_ms)
                    / NULLIF(SUM(SUM(duration_ms)) OVER (), 0), 1)             AS TotalRatio,
               AVG(CAST(duration_ms AS INTEGER))                               AS AvgMs,
               MAX(duration_ms)                                                AS MaxMs,
               SUM(cpu_ms)                                                     AS CpuTotalMs,
               AVG(CAST(cpu_ms AS INTEGER))                                    AS CpuAvgMs,
               MAX(cpu_ms)                                                     AS CpuMaxMs,
               AVG(row_count)                                                  AS RowsAvg,
               MAX(row_count)                                                  AS RowsMax,
               SUM(logical_reads)                                              AS ReadsTotal,
               AVG(logical_reads)                                              AS ReadsAvg,
               MAX(logical_reads)                                              AS ReadsMax,
               SUM(physical_reads)                                             AS PreadsTotal,
               AVG(physical_reads)                                             AS PreadsAvg,
               MAX(physical_reads)                                             AS PreadsMax,
               SUM(writes)                                                     AS WritesTotal,
               AVG(writes)                                                     AS WritesAvg,
               MAX(writes)                                                     AS WritesMax
        FROM dbpilot_slow_sql
        {where}
        GROUP BY fingerprint
        ORDER BY SUM(duration_ms) DESC
        LIMIT 50
        """;

    public string SlowSqlTemplatesFromTopSqlSql(string where) => $"""
        /* dbpilot */
        SELECT d.fingerprint                                                  AS Fingerprint,
               substr(MAX(t.sql_text), 1, 500)                                AS SampleSql,   -- 样例取字典序最大文本
               MAX(d.db_name)                                                 AS DbName,
               CAST(SUM(d.exec_count) AS INTEGER)                             AS Count,
               ROUND(100.0 * SUM(d.total_elapsed_ms)
                    / NULLIF(SUM(SUM(d.total_elapsed_ms)) OVER (), 0), 1)      AS TotalRatio,
               CAST(SUM(d.total_elapsed_ms) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS INTEGER)                AS AvgMs,       -- 均值=Σ耗时/Σ次数（累计窗口求和口径）
               MAX(d.max_elapsed_ms)                                          AS MaxMs,
               SUM(d.total_worker_ms)                                         AS CpuTotalMs,
               CAST(SUM(d.total_worker_ms) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS INTEGER)                AS CpuAvgMs,
               NULL                                                           AS CpuMaxMs,    -- delta 行无单次 CPU 上限
               NULL                                                           AS RowsAvg,
               NULL                                                           AS RowsMax,
               SUM(d.total_logical_reads)                                     AS ReadsTotal,
               ROUND(SUM(d.total_logical_reads) * 1.0
                    / NULLIF(SUM(d.exec_count), 0), 1)                         AS ReadsAvg,
               NULL                                                           AS ReadsMax,
               SUM(d.total_physical_reads)                                    AS PreadsTotal,
               ROUND(SUM(d.total_physical_reads) * 1.0
                    / NULLIF(SUM(d.exec_count), 0), 1)                         AS PreadsAvg,
               NULL                                                           AS PreadsMax,
               SUM(d.total_writes)                                            AS WritesTotal,
               ROUND(SUM(d.total_writes) * 1.0
                    / NULLIF(SUM(d.exec_count), 0), 1)                         AS WritesAvg,
               NULL                                                           AS WritesMax
        FROM dbpilot_top_sql_delta d
        INNER JOIN dbpilot_sql_template t ON t.instance_id = d.instance_id AND t.fingerprint = d.fingerprint
        {where}
        GROUP BY d.fingerprint
        ORDER BY SUM(d.total_elapsed_ms) DESC
        LIMIT 50
        """;

    public string SlowSqlTrendSql(string unit, string conds) => $"""
        /* dbpilot */
        SELECT {Bucket("event_time", unit)} AS TimeUtc,
               COUNT(*)             AS Count,
               SUM(duration_ms)     AS TotalMs
        FROM dbpilot_slow_sql
        WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
          {conds}
        GROUP BY {Bucket("event_time", unit)}
        ORDER BY TimeUtc
        """;

    public string IndexUsageLatestSnapshotSql(string dbCond) => $"""
        /* dbpilot */
        SELECT MAX(snapshot_time) FROM dbpilot_index_usage_snapshot
        WHERE instance_id = @instanceId{dbCond}
        """;

    public string IndexUsageTrendSql(string dbCond, string sysCond) => $"""
        /* dbpilot */
        SELECT snapshot_time AS SnapshotTime, SUM(used_page_count) AS TotalPages
        FROM dbpilot_index_usage_snapshot
        WHERE instance_id = @instanceId{dbCond}{sysCond}
        GROUP BY snapshot_time
        """;

    public string TopSqlHistorySql(string orderBy, int n) => $"""
        /* dbpilot */
        SELECT q.Fingerprint, q.DbName, q.SqlText, q.ExecutionCount, q.TotalElapsedMs, q.TotalWorkerMs,
               q.TotalLogicalReads, q.TotalPhysicalReads, q.TotalWrites, q.MaxElapsedMs, q.FirstSeen, q.LastSeen
        FROM (SELECT d.fingerprint                                              AS Fingerprint,
                     d.db_name                                                  AS DbName,
                     MAX(t.sql_text)                                            AS SqlText,
                     SUM(d.exec_count)                                          AS ExecutionCount,
                     SUM(d.total_elapsed_ms)                                    AS TotalElapsedMs,
                     SUM(d.total_worker_ms)                                     AS TotalWorkerMs,
                     SUM(d.total_logical_reads)                                 AS TotalLogicalReads,
                     SUM(d.total_physical_reads)                                AS TotalPhysicalReads,
                     SUM(d.total_writes)                                        AS TotalWrites,
                     MAX(d.max_elapsed_ms)                                      AS MaxElapsedMs,
                     MIN(d.window_start)                                        AS FirstSeen,
                     MAX(d.window_end)                                          AS LastSeen
              FROM dbpilot_top_sql_delta d
              LEFT JOIN dbpilot_sql_template t
                   ON t.instance_id = d.instance_id AND t.fingerprint = d.fingerprint
              WHERE d.instance_id = @instanceId
                AND (@db = '' OR d.db_name = @db)
                AND (@excludeSystemDb = 0 OR @db <> '' OR d.db_name NOT IN ('master', 'model', 'msdb', 'tempdb'))
                AND NOT EXISTS (SELECT 1 FROM dbpilot_top_sql_exclusion x WHERE x.fingerprint = d.fingerprint)
              GROUP BY d.fingerprint, d.db_name) q
        ORDER BY {orderBy} DESC
        LIMIT {n}
        """;

    // 与 @@IDENTITY 自增回填同款多语句批：同一会话内 changes() 紧跟 DELETE 取受影响行数；
    // SQLite 编译不带 DELETE..LIMIT，用 rowid 子查询限量（表中均有 INTEGER PRIMARY KEY 自增 rowid 别名）
    public string HousekeepingDeleteBatchSql(string table, string column, int batchSize) => $"""
        /* dbpilot */
        DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {column} < @cutoff LIMIT {batchSize});
        SELECT changes();
        """;

    public string InstanceLastErrorClearSql()
        => "/* dbpilot */ UPDATE dbpilot_instance SET last_error = NULL WHERE id = @id AND last_error IS NOT NULL";

    public string InstanceLastErrorSetSql()
        => "/* dbpilot */ UPDATE dbpilot_instance SET last_error = @error WHERE id = @id AND (last_error IS NULL OR last_error <> @error)";
}
