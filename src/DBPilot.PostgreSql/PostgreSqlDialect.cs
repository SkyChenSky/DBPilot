using DBPilot.Storage.Dialect;

namespace DBPilot.PostgreSql;

/// <summary>
/// PostgreSQL 13+ 平台库方言：以 MySqlDialect 为底本翻译——
/// DATE_FORMAT 桶→date_trunc 截断（timestamptz 列先 <c>AT TIME ZONE 'utc'</c> 落 UTC 墙钟再截断，
/// 会话时区无关）、LIMIT @skip,@take→LIMIT @take OFFSET @skip（PG 无双参 LIMIT 形态）、
/// 反引号标识符→双引号、SUBSTRING→substr、CAST AS SIGNED→CAST AS bigint、N''→''、
/// 布尔参数比较须写字面量 false（PG 无 bool/int 隐式 coercion，与 MySQL/SS 不同）。
/// DELETE 无 LIMIT 且无 ROW_COUNT()/changes() 对等物——CTE 删 + RETURNING 单语句带回受影响行数。
/// 对象名模糊过滤用 ILIKE（PG LIKE 大小写敏感，MySQL/SS 均不敏感，对齐用户输入匹配面）。
/// </summary>
public sealed class PostgreSqlDialect : IPlatformDialect
{
    /// <summary>时间桶截断等价表达式（按 unit 选粒度；AT TIME ZONE 'utc' 后截断墙钟值，与 SQL Server 语义一致）。</summary>
    private static string Bucket(string column, string unit) => unit switch
    {
        "minute" => $"date_trunc('minute', {column} AT TIME ZONE 'utc')",
        "hour"   => $"date_trunc('hour', {column} AT TIME ZONE 'utc')",
        "day"    => $"date_trunc('day', {column} AT TIME ZONE 'utc')",
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
        LIMIT @take OFFSET @skip
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
        SELECT s.Fingerprint, s.Cnt AS "Count", s.FirstTimeUtc, s.LastTimeUtc, e.id AS LastEventId
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
                WHERE r.event_id = e.id AND r.object_name ILIKE @objectName ESCAPE '\')
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
               COUNT(*)                                                        AS "Count",
               CAST(100.0 * SUM(duration_ms)
                    / NULLIF(SUM(SUM(duration_ms)) OVER (), 0) AS DECIMAL(10,1)) AS TotalRatio,
               CAST(AVG(duration_ms) AS bigint)                                AS AvgMs,
               MAX(duration_ms)                                                AS MaxMs,
               SUM(cpu_ms)                                                     AS CpuTotalMs,
               CAST(AVG(cpu_ms) AS bigint)                                     AS CpuAvgMs,
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
               CAST(SUM(d.exec_count) AS bigint)                              AS "Count",
               CAST(100.0 * SUM(d.total_elapsed_ms)
                    / NULLIF(SUM(SUM(d.total_elapsed_ms)) OVER (), 0) AS DECIMAL(10,1)) AS TotalRatio,
               CAST(SUM(d.total_elapsed_ms) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS bigint)                  AS AvgMs,       -- 均值=Σ耗时/Σ次数（累计窗口求和口径）
               MAX(d.max_elapsed_ms)                                          AS MaxMs,
               SUM(d.total_worker_ms)                                         AS CpuTotalMs,
               CAST(SUM(d.total_worker_ms) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS bigint)                  AS CpuAvgMs,
               NULL                                                           AS CpuMaxMs,    -- delta 行无单次 CPU 上限
               NULL                                                           AS RowsAvg,
               NULL                                                           AS RowsMax,
               SUM(d.total_logical_reads)                                     AS ReadsTotal,
               CAST(SUM(d.total_logical_reads) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS DECIMAL(18,1))          AS ReadsAvg,
               NULL                                                           AS ReadsMax,
               SUM(d.total_physical_reads)                                    AS PreadsTotal,
               CAST(SUM(d.total_physical_reads) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS DECIMAL(18,1))          AS PreadsAvg,
               NULL                                                           AS PreadsMax,
               SUM(d.total_writes)                                            AS WritesTotal,
               CAST(SUM(d.total_writes) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS DECIMAL(18,1))          AS WritesAvg,
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
               COUNT(*)             AS "Count",
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
                AND (@excludeSystemDb = false OR @db <> '' OR d.db_name NOT IN ('master', 'model', 'msdb', 'tempdb'))
                AND NOT EXISTS (SELECT 1 FROM dbpilot_top_sql_exclusion x WHERE x.fingerprint = d.fingerprint)
              GROUP BY d.fingerprint, d.db_name) q
        ORDER BY {orderBy} DESC
        LIMIT {n}
        """;

    // PG 的 DELETE 无 LIMIT、无 ROW_COUNT()/changes() 会话函数——CTE 删 + RETURNING 单语句带回受影响行数；
    // 限量经 id 子查询（Housekeeping 各表均有 id 主键）
    public string HousekeepingDeleteBatchSql(string table, string column, int batchSize) => $"""
        /* dbpilot */
        WITH del AS (DELETE FROM {table}
                     WHERE id IN (SELECT id FROM {table} WHERE {column} < @cutoff LIMIT {batchSize})
                     RETURNING 1)
        SELECT count(*) FROM del
        """;

    public string InstanceLastErrorClearSql()
        => "/* dbpilot */ UPDATE dbpilot_instance SET last_error = NULL WHERE id = @id AND last_error IS NOT NULL";

    public string InstanceLastErrorSetSql()
        => "/* dbpilot */ UPDATE dbpilot_instance SET last_error = @error WHERE id = @id AND (last_error IS NULL OR last_error <> @error)";
}
