using DBPilot.Storage.Dialect;

namespace DBPilot.SqlServer;

/// <summary>
/// SQL Server 平台库方言（B3a）：语句与收敛前 Core 内联版本逐字节等价——
/// ROW_NUMBER 子查询分页（2008 兼容）、DATEADD/DATEDIFF 桶、DELETE TOP + @@ROWCOUNT。
/// </summary>
public sealed class SqlServerDialect : IPlatformDialect
{
    public string DeadlockPageCountSql(string condSql) => $"""
        /* dbpilot */
        SELECT COUNT(*) FROM dbpilot_deadlock_event e
        WHERE e.instance_id = @instanceId AND e.event_time >= @start AND e.event_time < @end {condSql}
        """;

    // ROW_NUMBER() 子查询分页（2008 兼容，OFFSET/FETCH 是 2012+；设计文档 D1 双侧兼容 2008）
    public string DeadlockPageRowsSql(string condSql) => $"""
        /* dbpilot */
        SELECT Id, EventTime, VictimSpids, Fingerprint FROM (
            SELECT e.id AS Id, e.event_time AS EventTime,
                   e.victim_spids AS VictimSpids, e.fingerprint AS Fingerprint,
                   ROW_NUMBER() OVER (ORDER BY e.event_time DESC) AS rn
            FROM dbpilot_deadlock_event e
            WHERE e.instance_id = @instanceId AND e.event_time >= @start AND e.event_time < @end {condSql}
        ) t WHERE t.rn > @skip AND t.rn <= @skip + @take
        ORDER BY t.rn
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
        SELECT DATEADD({unit}, DATEDIFF({unit}, 0, event_time), 0) AS TimeUtc, COUNT(*) AS Total
        FROM dbpilot_deadlock_event
        WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
        GROUP BY DATEADD({unit}, DATEDIFF({unit}, 0, event_time), 0)
        """;

    public string DeadlockTrendColorsSql(string unit) => $"""
        /* dbpilot */
        SELECT DATEADD({unit}, DATEDIFF({unit}, 0, event_time), 0) AS TimeUtc,
               COUNT(DISTINCT CASE WHEN resource_type = 'keylock'    THEN event_id END) AS KeyLocks,
               COUNT(DISTINCT CASE WHEN resource_type = 'objectlock' THEN event_id END) AS ObjectLocks,
               COUNT(DISTINCT CASE WHEN resource_type = 'pagelock'   THEN event_id END) AS PageLocks,
               COUNT(DISTINCT CASE WHEN resource_type = 'ridlock'    THEN event_id END) AS RidLocks,
               COUNT(DISTINCT CASE WHEN resource_type NOT IN ('keylock','objectlock','pagelock','ridlock')
                                   THEN event_id END) AS OtherLocks
        FROM dbpilot_deadlock_resource
        WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
        GROUP BY DATEADD({unit}, DATEDIFF({unit}, 0, event_time), 0)
        """;

    public string DeadlockFingerprintStatsSql() => """
        /* dbpilot */
        SELECT s.Fingerprint, s.Cnt AS [Count], s.FirstTimeUtc, s.LastTimeUtc, e.id AS LastEventId
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
        ORDER BY s.Cnt DESC;
        """;

    public string DeadlockObjectFilterSql() => """
        EXISTS (SELECT 1 FROM dbpilot_deadlock_resource r
                WHERE r.event_id = e.id AND r.object_name LIKE @objectName ESCAPE N'\')
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
          AND (@db = N'' OR db_name = @db)
        ORDER BY last_seen_utc DESC
        """;

    public string PlanChangesTopSql() => """
        /* dbpilot */
        SELECT TOP (200) id AS Id, old_plan_hash AS OldPlanHash, new_plan_hash AS NewPlanHash,
               old_avg_elapsed_ms AS OldAvgElapsedMs, new_avg_elapsed_ms AS NewAvgElapsedMs,
               old_avg_worker_ms AS OldAvgWorkerMs, new_avg_worker_ms AS NewAvgWorkerMs,
               old_avg_reads AS OldAvgReads, new_avg_reads AS NewAvgReads,
               old_exec_count AS OldExecCount, new_exec_count AS NewExecCount,
               changed_at_utc AS ChangedAtUtc
        FROM dbpilot_plan_change
        WHERE instance_id = @instanceId AND fingerprint = @fingerprint
        ORDER BY changed_at_utc DESC
        """;

    public string PlanChangeBoardSql(int n) => $"""
        /* dbpilot */
        SELECT TOP ({n}) c.id AS Id, c.instance_id AS InstanceId, c.fingerprint AS Fingerprint, c.db_name AS DbName,
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
        """;

    public string SlowSqlTemplatesSql(string where) => $"""
        /* dbpilot */
        SELECT TOP 50 fingerprint                                              AS Fingerprint,
               SUBSTRING(MAX(sql_text), 1, 500)                                AS SampleSql,   -- 样例取字典序最大文本（同类模板文本相近）
               MAX(db_name)                                                    AS DbName,
               COUNT(*)                                                        AS Count,
               CAST(100.0 * SUM(duration_ms)
                    / NULLIF(SUM(SUM(duration_ms)) OVER (), 0) AS decimal(10,1)) AS TotalRatio,
               AVG(CAST(duration_ms AS bigint))                                AS AvgMs,
               MAX(duration_ms)                                                AS MaxMs,
               SUM(cpu_ms)                                                     AS CpuTotalMs,
               AVG(CAST(cpu_ms AS bigint))                                     AS CpuAvgMs,
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
        ORDER BY SUM(duration_ms) DESC;
        """;

    public string SlowSqlTemplatesFromTopSqlSql(string where) => $"""
        /* dbpilot */
        SELECT TOP 50 d.fingerprint                                          AS Fingerprint,
               SUBSTRING(MAX(t.sql_text), 1, 500)                            AS SampleSql,   -- 样例取字典序最大文本
               MAX(d.db_name)                                                AS DbName,
               CAST(SUM(d.exec_count) AS int)                                AS Count,
               CAST(100.0 * SUM(d.total_elapsed_ms)
                    / NULLIF(SUM(SUM(d.total_elapsed_ms)) OVER (), 0) AS decimal(10,1)) AS TotalRatio,
               CAST(SUM(d.total_elapsed_ms) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS bigint)                AS AvgMs,       -- 均值=Σ耗时/Σ次数（累计窗口求和口径）
               MAX(d.max_elapsed_ms)                                         AS MaxMs,
               SUM(d.total_worker_ms)                                        AS CpuTotalMs,
               CAST(SUM(d.total_worker_ms) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS bigint)                AS CpuAvgMs,
               NULL                                                          AS CpuMaxMs,    -- delta 行无单次 CPU 上限
               NULL                                                          AS RowsAvg,
               NULL                                                          AS RowsMax,
               SUM(d.total_logical_reads)                                    AS ReadsTotal,
               CAST(SUM(d.total_logical_reads) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS decimal(18,1))         AS ReadsAvg,
               NULL                                                          AS ReadsMax,
               SUM(d.total_physical_reads)                                   AS PreadsTotal,
               CAST(SUM(d.total_physical_reads) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS decimal(18,1))         AS PreadsAvg,
               NULL                                                          AS PreadsMax,
               SUM(d.total_writes)                                           AS WritesTotal,
               CAST(SUM(d.total_writes) * 1.0
                    / NULLIF(SUM(d.exec_count), 0) AS decimal(18,1))         AS WritesAvg,
               NULL                                                          AS WritesMax
        FROM dbpilot_top_sql_delta d
        INNER JOIN dbpilot_sql_template t ON t.instance_id = d.instance_id AND t.fingerprint = d.fingerprint
        {where}
        GROUP BY d.fingerprint
        ORDER BY SUM(d.total_elapsed_ms) DESC;
        """;

    public string SlowSqlTrendSql(string unit, string conds) => $"""
        /* dbpilot */
        SELECT DATEADD({unit}, DATEDIFF({unit}, 0, event_time), 0) AS TimeUtc,
               COUNT(*)             AS Count,
               SUM(duration_ms)     AS TotalMs
        FROM dbpilot_slow_sql
        WHERE instance_id = @instanceId AND event_time >= @start AND event_time < @end
          {conds}
        GROUP BY DATEADD({unit}, DATEDIFF({unit}, 0, event_time), 0)
        ORDER BY TimeUtc;
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
        SELECT TOP ({n}) q.Fingerprint, q.DbName, q.SqlText, q.ExecutionCount, q.TotalElapsedMs, q.TotalWorkerMs,
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
                AND (@db = N'' OR d.db_name = @db)
                AND (@excludeSystemDb = 0 OR @db <> N'' OR d.db_name NOT IN (N'master', N'model', N'msdb', N'tempdb'))
                AND NOT EXISTS (SELECT 1 FROM dbpilot_top_sql_exclusion x WHERE x.fingerprint = d.fingerprint)
              GROUP BY d.fingerprint, d.db_name) q
        ORDER BY {orderBy} DESC
        """;

    // DELETE 本身不产生结果集（SqlQuery 读不到受影响行），紧跟 SELECT @@ROWCOUNT 带回
    public string HousekeepingDeleteBatchSql(string table, string column, int batchSize) => $"""
        /* dbpilot */
        DELETE TOP ({batchSize}) FROM {table} WHERE {column} < @cutoff;
        SELECT @@ROWCOUNT;
        """;

    public string InstanceLastErrorClearSql()
        => "/* dbpilot */ UPDATE dbpilot_instance SET last_error = NULL WHERE id = @id AND last_error IS NOT NULL";

    public string InstanceLastErrorSetSql()
        => "/* dbpilot */ UPDATE dbpilot_instance SET last_error = @error WHERE id = @id AND (last_error IS NULL OR last_error <> @error)";
}
