using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Chloe.SqlServer;
using DBPilot.Common;
using DBPilot.Core.Blocking;
using DBPilot.Core.Deadlocks;
using DBPilot.Core.Indexes;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.Providers;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.SlowSql;
using DBPilot.Core.TopSql;
using Microsoft.Data.SqlClient;

namespace DBPilot.SqlServer;

/// <summary>
/// SQL Server Provider：连接测试 / 版本探测 / 库列表 / TopSQL / 阻塞 / 死锁 / 慢SQL / 计划 / 指标采集。
/// 数据访问与平台库组件统一，全部走 Chloe：原生 SQL 用 SqlQuery（立即执行、按列名映射 POCO），
/// 因其内部同步执行，对外以 Task.Run 包装避免阻塞调用方线程。
/// 实例连接按配置动态创建 MsSqlContext（区别于平台库固定连接的 DBPilotSqlServerContext）。
/// 连接串统一 Encrypt=False + TrustServerCertificate=True（内部网络 + 2008 老实例兼容）。
/// </summary>
[DbpilotEngine(DbpilotEngines.SqlServer)]
[DbpilotCapabilities(None = [DbpilotCapabilityKeys.SlowSqlTemplates])]
public partial class SqlServerProvider : IDatabaseProvider
{

    /// <summary>连接测试：SELECT 1 建连并测延迟，成功后做服务器级权限自检（缺失权限清单）。</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new ConnectionTestResult();
            var watch = Stopwatch.StartNew();

            try
            {
                var context = CreateContext(cfg);

                // SELECT 1 触发建连（等价于 Open + 校验可查询）
                context.SqlQuery<int>("/* dbpilot */ SELECT 1 AS Value");
                watch.Stop();

                result.Ok = true;
                result.LatencyMs = (int)watch.ElapsedMilliseconds;

                // 权限自检：服务器级有效权限对照清单
                var permissions = context.SqlQuery<string>(
                    "/* dbpilot */ SELECT permission_name AS Value FROM fn_my_permissions(NULL, 'SERVER')");

                result.MissingPermissions = PermissionCatalog.FindMissing(permissions);

                // 库级访问自检：VIEW ANY DATABASE 让 sys.databases 全库可见，但账号在库内无用户映射时
                // 无法进入该库（索引诊断逐库执行会整库跳过、页面静默缺数据）——HAS_DBACCESS 直接判定
                var inaccessible = context.SqlQuery<string>("""
                    /* dbpilot */
                    SELECT name AS Value
                    FROM sys.databases
                    WHERE state = 0 AND source_database_id IS NULL
                      AND database_id > 4
                      AND HAS_DBACCESS(name) = 0
                    ORDER BY name
                    """);

                if (inaccessible.Count > 0)
                {
                    var login = cfg.LoginName;
                    result.MissingPermissions.Add(new MissingPermission
                    {
                        Permission = $"业务库访问（{inaccessible.Count} 个库无该账号的用户映射）",
                        Impact = $"索引诊断（缺失索引 / 使用率 / 碎片）无法进入这些库，对应数据不会出现在诊断结果：{string.Join("、", inaccessible.Select(d => $"[{d}]")).Sub(180)}",
                        FixScript = string.Join("\n", inaccessible.Take(5).Select(d => $"""
                            USE [{d}];
                            CREATE USER [{login}] FOR LOGIN [{login}];
                            GRANT VIEW DEFINITION TO [{login}];
                            GRANT VIEW DATABASE STATE TO [{login}];
                            """)) + (inaccessible.Count > 5 ? $"\n-- 其余 {inaccessible.Count - 5} 个库同理" : ""),
                    });
                }
            }
            catch (Exception ex)
            {
                watch.Stop();
                result.Ok = false;
                result.LatencyMs = (int)watch.ElapsedMilliseconds;
                result.Error = ex.GetDeepestException().Message;
            }

            return result;
        }, ct);

    /// <summary>版本/环境探测：SERVERPROPERTY + dm_os_sys_info 采集版本、内核数、实例启动时间、默认日志目录与时钟偏移。</summary>
    public Task<InstanceMeta> ProbeAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // 版本探测（全版本兼容：tempdb create_date 代替实例启动时间 DMV）；
            // ErrorLogFileName 顺带取默认日志目录（xe_file_path 留空自动探测回填，公开属性无权限要求）
            const string sql = """
                /* dbpilot */
                SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(32))  AS ProductVersion,
                       CAST(SERVERPROPERTY('Edition') AS nvarchar(64))         AS Edition,
                       CAST(SERVERPROPERTY('MachineName') AS nvarchar(128))    AS MachineName,
                       si.cpu_count                                            AS CpuCores,
                       SYSUTCDATETIME()                                        AS UtcNow,
                       CAST(SERVERPROPERTY('ErrorLogFileName') AS nvarchar(512)) AS ErrorLogFileName,
                       (SELECT create_date FROM sys.databases WHERE name = N'tempdb') AS InstanceStartTime
                FROM sys.dm_os_sys_info si
                """;

            var row = CreateContext(cfg).SqlQuery<ProbeRow>(sql).FirstOrDefault()
                      ?? throw new InvalidOperationException("版本探测无返回行");

            var utcNow = DateTime.SpecifyKind(row.UtcNow, DateTimeKind.Utc);
            var meta = new InstanceMeta
            {
                ProductVersion = row.ProductVersion ?? "",
                Edition = row.Edition ?? "",
                MachineName = row.MachineName,
                CpuCores = row.CpuCores,
                DefaultLogPath = DeriveLogDirectory(row.ErrorLogFileName),
                SqlServerStartTimeUtc = row.InstanceStartTime is null
                    ? null
                    : DateTime.SpecifyKind(row.InstanceStartTime.Value, DateTimeKind.Utc),
                ClockSkewSeconds = (int)Math.Abs((utcNow - DateTime.UtcNow).TotalSeconds)
            };

            // MajorVersion：10=2008、10.5→105、11=2012 …
            meta.MajorVersion = ParseMajorVersion(meta.ProductVersion);
            return meta;
        }, ct);

    /// <summary>库列表：在线（state=0）且非快照库，按名称排序。</summary>
    public Task<List<string>> GetDatabasesAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // state=0 在线且非快照库（快照判断用 source_database_id）
            const string sql = """
                /* dbpilot */
                SELECT name AS Value FROM sys.databases
                WHERE state = 0 AND source_database_id IS NULL
                ORDER BY name
                """;

            return CreateContext(cfg).SqlQuery<string>(sql);
        }, ct);

    /// <summary>缺失索引建议（Initial Catalog 指定目标库，score 微软算法 SQL 端计算）。</summary>
    public Task<List<MissingIndexItem>> GetMissingIndexesAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT DB_NAME()                                                     AS DbName,
                       mid.statement                                                  AS TableName,
                       mid.equality_columns                                           AS EqualityColumns,
                       mid.inequality_columns                                         AS InequalityColumns,
                       mid.included_columns                                           AS IncludedColumns,
                       migs.user_seeks                                                AS UserSeeks,
                       migs.user_scans                                                AS UserScans,
                       migs.avg_total_user_cost                                       AS AvgTotalUserCost,
                       migs.avg_user_impact                                           AS AvgUserImpact,
                       DATEADD(MILLISECOND, tz.tz_ms, migs.last_user_seek)             AS LastUserSeek,
                       migs.user_seeks * migs.avg_total_user_cost * migs.avg_user_impact * 0.01 AS Score,
                       tp.table_pages                                                 AS TablePages,
                       tp.table_rows                                                  AS TableRows
                FROM sys.dm_db_missing_index_group_stats migs
                INNER JOIN sys.dm_db_missing_index_groups mig  ON mig.index_group_handle = migs.group_handle
                INNER JOIN sys.dm_db_missing_index_details mid ON mid.index_handle = mig.index_handle
                OUTER APPLY (SELECT SUM(p.used_page_count) AS table_pages, SUM(p.row_count) AS table_rows
                             FROM sys.dm_db_partition_stats p
                             WHERE p.object_id = mid.object_id AND p.index_id IN (0, 1)) tp
                -- tz_ms = utc − local（DATEADD(+tz_ms, 本地时间列) = UTC）；全文件 6 处 CROSS APPLY 同此方向
                CROSS APPLY (SELECT DATEDIFF(MILLISECOND, SYSDATETIME(), SYSUTCDATETIME()) AS tz_ms) tz
                WHERE mid.database_id = DB_ID()
                ORDER BY Score DESC
                """;

            return CreateContext(cfg, dbName).SqlQuery<MissingIndexItem>(sql);
        }, ct);

    /// <summary>
    /// 索引使用率：从 sys.indexes 出发 LEFT JOIN 使用统计 ——
    /// 从未被触碰的索引在 dm_db_index_usage_stats 无行（INNER JOIN 会漏掉，恰恰是最该报"未使用"的），
    /// 无行按 0 处理；键列 FOR XML 聚合、页数分区聚合、last_user_* 服务器本地时间转 UTC。
    /// </summary>
    public Task<List<IndexUsageItem>> GetIndexUsageAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT DB_NAME()                                                     AS DbName,
                       QUOTENAME(OBJECT_SCHEMA_NAME(i.object_id)) + '.' + QUOTENAME(o.name) AS TableName,
                       i.name                                                            AS IndexName,
                       i.type_desc                                                      AS TypeDesc,
                       i.is_primary_key                                                 AS IsPrimaryKey,
                       i.is_unique                                                      AS IsUnique,
                       i.filter_definition                                              AS FilterDefinition,
                       STUFF((SELECT ',' + c.name
                              FROM sys.index_columns ic
                              INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                              ORDER BY ic.key_ordinal
                              FOR XML PATH('')), 1, 1, '')                             AS KeyColumns,
                       ISNULL(u.user_seeks, 0)                                          AS UserSeeks,
                       ISNULL(u.user_scans, 0)                                          AS UserScans,
                       ISNULL(u.user_lookups, 0)                                        AS UserLookups,
                       ISNULL(u.user_updates, 0)                                        AS UserUpdates,
                       DATEADD(MILLISECOND, tz.tz_ms, u.last_user_seek)                 AS LastUserSeek,
                       DATEADD(MILLISECOND, tz.tz_ms, u.last_user_scan)                 AS LastUserScan,
                       DATEADD(MILLISECOND, tz.tz_ms, u.last_user_update)               AS LastUserUpdate,
                       ps.used_page_count                                               AS UsedPageCount
                FROM sys.indexes i
                INNER JOIN sys.objects o ON o.object_id = i.object_id
                LEFT JOIN sys.dm_db_index_usage_stats u
                       ON u.object_id = i.object_id AND u.index_id = i.index_id AND u.database_id = DB_ID()
                OUTER APPLY (SELECT SUM(p.used_page_count) AS used_page_count
                             FROM sys.dm_db_partition_stats p
                             WHERE p.object_id = i.object_id AND p.index_id = i.index_id) ps
                CROSS APPLY (SELECT DATEDIFF(MILLISECOND, SYSDATETIME(), SYSUTCDATETIME()) AS tz_ms) tz
                WHERE o.is_ms_shipped = 0
                  AND i.index_id > 0
                  AND i.name IS NOT NULL
                  AND i.is_disabled = 0
                """;

            return CreateContext(cfg, dbName).SqlQuery<IndexUsageItem>(sql);
        }, ct);

    /// <summary>
    /// 碎片扫描候选表：用户表（is_ms_shipped=0）+ 堆/聚集分区页数聚合（index_id IN (0,1)），
    /// HAVING 过滤 minPages（默认 256 起扫），按页数降序（大表优先）。
    /// </summary>
    public Task<List<FragTableInfo>> GetFragmentTablesAsync(InstanceConfig cfg, string dbName, int minPages, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT t.object_id AS ObjectId,
                       QUOTENAME(s.name) + '.' + QUOTENAME(t.name) AS TableName,
                       SUM(p.used_page_count) AS PageCount
                FROM sys.tables t
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                INNER JOIN sys.dm_db_partition_stats p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                WHERE t.is_ms_shipped = 0
                GROUP BY t.object_id, s.name, t.name
                HAVING SUM(p.used_page_count) >= @minPages
                ORDER BY PageCount DESC
                """;

            return CreateContext(cfg, dbName).SqlQuery<FragTableInfo>(sql, new { minPages });
        }, ct);

    /// <summary>
    /// 单表碎片：dm_db_index_physical_stats LIMITED 模式（最省资源，不做全页扫描），
    /// 仅返回命名索引（index_id &gt; 0），页数下限同 minPages。
    /// </summary>
    public Task<List<IndexFragmentItem>> GetIndexFragmentationAsync(InstanceConfig cfg, string dbName, int objectId, int minPages, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT DB_NAME() AS DbName,
                       QUOTENAME(OBJECT_SCHEMA_NAME(i.object_id)) + '.' + QUOTENAME(OBJECT_NAME(i.object_id)) AS TableName,
                       i.name AS IndexName,
                       ips.index_type_desc AS IndexType,
                       ips.partition_number AS PartitionNumber,
                       ips.avg_fragmentation_in_percent AS AvgFragmentationPercent,
                       ips.page_count AS PageCount,
                       ips.record_count AS RecordCount
                FROM sys.dm_db_index_physical_stats(DB_ID(), @objectId, NULL, NULL, 'LIMITED') ips
                INNER JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
                WHERE ips.index_id > 0
                  AND i.name IS NOT NULL
                  AND ips.page_count >= @minPages
                ORDER BY ips.index_id, ips.partition_number
                """;

            return CreateContext(cfg, dbName).SqlQuery<IndexFragmentItem>(sql, new { objectId, minPages });
        }, ct);

    /// <summary>
    /// 实时 Top SQL：dm_exec_query_stats 按 库 + query_hash 分组聚合（对齐阿里云：跨库同语句分行；
    /// 同库同语句多执行计划合并），statement 文本按 start/end offset 截取（字节偏移 / 2），
    /// last_execution_time 本地时间转 UTC；时间列为微秒累计（平台侧 /1000 转 ms）。
    /// db 为空 = 全部库。库名：dm_exec_sql_text.dbid 对 ad hoc/动态 SQL 按设计为 NULL（微软已知反馈项），
    /// 用计划属性 dm_exec_plan_attributes('dbid')（计划编译时库上下文）兜底 —— 否则选具体库会把
    /// 绝大多数 ad hoc 行全部过滤掉。
    ///
    /// RDS 内部查询排除（对齐阿里云 DAS 只统计用户 SQL；阿里云靠内部登录身份识别自家采集，DMV 无此信息，
    /// 用以下分层规则逼近）：
    /// ① st.text IS NULL 剔除 —— 文本不可解析（RDS 受限查询/** Restricted Text **、master 的 _$$_ 管理过程）；
    /// ② '/* rds internal mark */' 前缀剔除 —— RDS 代理官方自留标记（实测 290 行，master/tempdb/用户库均有）；
    /// ③ '%/* dbpilot */%' 包含剔除 —— 平台自身监控 SQL（Provider 语句统一携带标记，防自采集污染榜单）；
    /// ④ filter.Patterns 特征黑名单（配置 DBPilot:TopSqlExcludePatterns）—— 无标记的 RDS 巡检脚本
    ///    （sys.configurations/sys.databases 轮询、DBCC TRACESTATUS、sys.traces 读取等，实测约 600 行/21 万次执行），
    ///    新增噪音改配置即可，不动代码；
    /// ⑤ filter.Fingerprints 指纹黑名单（平台库 dbpilot_top_sql_exclusion，页面“排除”按钮维护）——
    ///    query_hash 精确匹配，零误伤，跨实例全局生效；
    /// ⑥ “全部库”时排除 msdb/model 行（引擎/Agent 内部活动，非用户应用查询；DbName 为 NULL 的 ad hoc 行
    ///    保留 —— NOT IN 对 NULL 三值逻辑坑需显式 OR）；单独选系统库仍可查看；
    /// ⑦ filter.ExcludeSystemDb=true（页面“排除系统库”按钮传入，兜底开关）：⑥ 基础上再排除 master 行
    ///    （④ 覆盖不全时的一键降噪，代价是 master 上下文的用户 SQL 不可见）；tempdb 永不按库排除
    ///    （用户临时表查询在 tempdb 上下文，阿里云榜上实测可见）。
    /// </summary>
    public Task<List<TopSqlRawRow>> GetTopSqlRealtimeAsync(InstanceConfig cfg, string db, TopSqlFilter? filter = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var sql = BuildTopSqlSql(filter ?? new TopSqlFilter());
            return CreateContext(cfg).SqlQuery<TopSqlRawRow>(sql, new { db });
        }, ct);

    /// <summary>
    /// 活动请求采样：dm_exec_requests（session_id ≥ 50）+ 会话信息 +
    /// statement 文本（sql_handle + offset 截取，offset 为 -1 的系统行取全文）；
    /// total_elapsed_time μs→ms、start_time 本地时间→UTC 均在 SQL 端。全量返回（通常几十行内），
    /// 阻塞树组装在平台侧 Core，性能洞察（AAS）采样复用本查询。
    /// </summary>
    public Task<List<ActiveRequestRow>> GetActiveRequestsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() => CreateContext(cfg).SqlQuery<ActiveRequestRow>(ActiveRequestsSql), ct);

    /// <summary>主查询（常量供单测断言）：排除平台自监控连接（program_name='DBPilot'，对齐 XE
    /// client_app_name 口径——(SS,SS) 同机形态下平台落库会进采样抬高 AAS）。</summary>
    internal const string ActiveRequestsSql = """
        /* dbpilot */
        SELECT r.session_id                AS SessionId,
               r.status                    AS Status,
               r.command                   AS Command,
               DATEADD(MILLISECOND, tz.tz_ms, r.start_time)                       AS StartTimeUtc,
               r.wait_type                 AS WaitType,
               r.wait_time                 AS WaitTimeMs,
               r.wait_resource             AS WaitResource,
               r.blocking_session_id       AS BlockingSessionId,
               r.total_elapsed_time / 1000 AS TotalElapsedMs,
               r.cpu_time / 1000           AS CpuMs,
               r.open_transaction_count    AS OpenTranCount,
               s.login_name                AS LoginName,
               s.host_name                 AS HostName,
               s.program_name              AS ProgramName,
               DB_NAME(r.database_id)      AS DbName,
               SUBSTRING(st.text, (r.statement_start_offset / 2) + 1,
                   ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                                                 ELSE r.statement_end_offset END
                     - r.statement_start_offset) / 2) + 1)                        AS SqlText,
               st.text                                                                     AS BatchSqlText,
               qh.query_hash_hex                                                           AS QueryHash
        FROM sys.dm_exec_requests r
        LEFT JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) st
        OUTER APPLY (SELECT TOP 1 CONVERT(varchar(32), qs.query_hash, 2) AS query_hash_hex
                     FROM sys.dm_exec_query_stats qs
                     WHERE qs.sql_handle = r.sql_handle
                       AND qs.statement_start_offset = r.statement_start_offset
                       AND qs.statement_end_offset = r.statement_end_offset) qh
        CROSS APPLY (SELECT DATEDIFF(MILLISECOND, SYSDATETIME(), SYSUTCDATETIME()) AS tz_ms) tz
        WHERE r.session_id >= 50
          AND r.session_id <> @@SPID
          AND ISNULL(s.program_name, '') <> 'DBPilot'   /* 平台自监控连接不进采样（对齐 XE client_app_name 口径），(SS,SS) 同机形态下防止平台落库抬高 AAS */
        """;

    /// <summary>
    /// 头阻塞者补查：dm_exec_requests 无该会话行 = "睡着拿锁"
    /// （事务开着锁拿着但没有活动请求）。headSessionIds 由平台侧去重后传入（拼 IN 列表，int 无注入风险）。
    /// ① 事务计数：dm_tran_session_transactions 无 open_tran_count 列（设计文档笔误），改子查询 COUNT（全版本）；
    /// ② 最后执行语句版本/能力分支（BuildHeadBlockerLastSql）：MajorVersion ≥ 12（2014+）用
    ///    sys.dm_exec_input_buffer（且信息更准：会话当前输入缓冲）；老版本按列存在性——RDS 安全改造
    ///    会剥掉 dm_exec_sessions.most_recent_sql_handle（实测 RDS 2012 SP4，版本号无法判定），
    ///    无列时兜底 dm_exec_connections.most_recent_sql_handle（RDS 上保留）。
    /// </summary>
    public Task<List<HeadBlockerRow>> GetHeadBlockersAsync(InstanceConfig cfg, List<int> headSessionIds, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // 第二参短路：≥12 走 input_buffer 分支，不触发列存在性探测（省一次元数据查询）
            var (lastSql, lastSqlCol) = BuildHeadBlockerLastSql(
                cfg.MajorVersion, cfg.MajorVersion >= 12 || HasSessionsMostRecentSqlHandle(cfg));

            var sql = $"""
                /* dbpilot */
                SELECT s.session_id          AS SessionId,
                       s.login_name          AS LoginName,
                       s.host_name           AS HostName,
                       s.program_name        AS ProgramName,
                       DB_NAME(s.database_id) AS DbName,
                       tc.open_tran_count    AS OpenTranCount,
                       DATEADD(MILLISECOND, tz.tz_ms, t.transaction_begin_time)  AS TransactionBeginUtc,
                       {lastSqlCol}          AS LastSqlText
                FROM sys.dm_exec_sessions s
                LEFT JOIN sys.dm_tran_session_transactions tst ON tst.session_id = s.session_id
                LEFT JOIN sys.dm_tran_active_transactions t   ON t.transaction_id = tst.transaction_id
                OUTER APPLY (SELECT COUNT(*) AS open_tran_count
                             FROM sys.dm_tran_session_transactions t2
                             WHERE t2.session_id = s.session_id) tc
                {lastSql}
                CROSS APPLY (SELECT DATEDIFF(MILLISECOND, SYSDATETIME(), SYSUTCDATETIME()) AS tz_ms) tz
                WHERE s.session_id IN ({string.Join(", ", headSessionIds)})
                """;

            return CreateContext(cfg).SqlQuery<HeadBlockerRow>(sql);
        }, ct);

    /// <summary>头阻塞补查"最后语句"的版本/能力分支（纯函数，供单测）：见方法上方注释 ②。
    /// 兜底分支 JOIN 与 APPLY 同行——SQL 对此处空白不敏感，与另两分支单行形态保持一致。</summary>
    internal static (string ApplySql, string LastSqlCol) BuildHeadBlockerLastSql(int majorVersion, bool hasSessionsMostRecentSqlHandle)
        => majorVersion >= 12
            ? ("OUTER APPLY sys.dm_exec_input_buffer(s.session_id, NULL) ib", "ib.event_info")
            : hasSessionsMostRecentSqlHandle
                ? ("OUTER APPLY sys.dm_exec_sql_text(s.most_recent_sql_handle) ib", "ib.text")
                : ("LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) ib",
                   "ib.text");

    /// <summary>dm_exec_sessions.most_recent_sql_handle 列存在性探测结果（实例 host:port → 是否可用）。
    /// 元数据查询一次进程内缓存（列存在性运行期不变；探测本身幂等，并发重复执行无害）。</summary>
    private static readonly ConcurrentDictionary<string, bool> SessionsSqlHandleCache = new();

    /// <summary>RDS 安全改造会剥掉 dm_exec_sessions.most_recent_sql_handle（版本号无法判定，
    /// 实测 RDS 2012 SP4 无该列）——查 sys.all_columns 元数据实测判定，每实例探测一次。</summary>
    private bool HasSessionsMostRecentSqlHandle(InstanceConfig cfg)
        => SessionsSqlHandleCache.GetOrAdd($"{cfg.Host}:{cfg.Port}", _ =>
            CreateContext(cfg).SqlQuery<int>("""
                /* dbpilot */
                SELECT COUNT(*) AS Value
                FROM sys.all_columns
                WHERE object_id = OBJECT_ID('sys.dm_exec_sessions') AND name = 'most_recent_sql_handle'
                """).FirstOrDefault() > 0);

    /// <summary>
    /// 阻塞原因（锁资源）：dm_tran_locks 查涉及会话的锁（WAIT = 正在等的锁、GRANT = 已持有的锁），
    /// 过滤 DATABASE 等噪音类型只留对象级（OBJECT/KEY/PAGE/RID/HOBT/EXTENT）；
    /// resource_associated_entity_id 按 object_id / hobt_id(partition_id) / allocation_unit_id
    /// 三种可能分库（3 段名动态拼接，库名转义 ] → ]]，bigint 值拼 VALUES 无注入风险）反查 schema.表名。
    /// </summary>
    public Task<List<SessionLockRow>> GetSessionLocksAsync(InstanceConfig cfg, List<int> sessionIds, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var sql = $"""
                /* dbpilot */
                SELECT l.request_session_id             AS SessionId,
                       l.resource_type                  AS ResourceType,
                       DB_NAME(l.resource_database_id)  AS DbName,
                       l.resource_associated_entity_id  AS EntityId,
                       l.request_mode                   AS LockMode,
                       l.request_status                 AS LockStatus
                FROM sys.dm_tran_locks l
                WHERE l.request_type = 'LOCK'
                  AND l.resource_type IN ('OBJECT', 'HOBT', 'EXTENT', 'PAGE', 'KEY', 'RID')
                  AND l.request_session_id IN ({string.Join(", ", sessionIds)})
                """;

            var ctx = CreateContext(cfg);
            var rows = ctx.SqlQuery<SessionLockRow>(sql);

            // 实体 id → 对象名（按库分组批量反查；未命中保留 null）
            foreach (var g in rows.Where(r => r.EntityId != 0 && !string.IsNullOrEmpty(r.DbName))
                                  .GroupBy(r => r.DbName!))
            {
                var values = string.Join(", ", g.Select(r => r.EntityId).Distinct().Select(id => $"({id})"));
                var db = g.Key.Replace("]", "]]");
                // TOP 1 收敛为单行：三个 id 序列（object_id / partition_id / allocation_unit_id）
                // 数值区间重叠，同一实体 id 可能命中多条路径（曾致 ToDictionary 重复键异常）
                var resolve = $"""
                    /* dbpilot */
                    SELECT e.entity_id AS EntityId, s.name + '.' + o.name AS ObjectName
                    FROM (VALUES {values}) e(entity_id)
                    OUTER APPLY (
                        SELECT TOP 1 y.obj
                        FROM (SELECT o2.object_id AS obj FROM [{db}].sys.objects o2 WHERE o2.object_id = e.entity_id
                              UNION ALL
                              SELECT p.object_id FROM [{db}].sys.partitions p WHERE p.partition_id = e.entity_id
                              UNION ALL
                              SELECT p2.object_id FROM [{db}].sys.allocation_units au
                                JOIN [{db}].sys.partitions p2 ON p2.partition_id = au.container_id
                              WHERE au.allocation_unit_id = e.entity_id) y
                    ) x
                    LEFT JOIN [{db}].sys.objects o ON o.object_id = x.obj
                    LEFT JOIN [{db}].sys.schemas s ON s.schema_id = o.schema_id
                    """;

                var names = ctx.SqlQuery<LockEntityNameRow>(resolve)
                    .GroupBy(r => r.EntityId)
                    .ToDictionary(g => g.Key, g => g.First());   // 兜底防重：极端情况下仍多行时取首条
                foreach (var r in g)
                    if (names.TryGetValue(r.EntityId, out var n))
                        r.ObjectName = n.ObjectName;
            }

            return rows;
        }, ct);

    /// <summary>锁实体反查行（GetSessionLocksAsync 内部用）。</summary>
    private class LockEntityNameRow
    {
        public long EntityId { get; set; }
        public string? ObjectName { get; set; }
    }

    // ---------- 死锁采集（版本双路径） ----------

    /// <summary>死锁事件增量读取：版本双路径（2012+ system_health event_file / 2008 自建会话，创建失败降级 ring_buffer）。</summary>
    public Task<DeadlockReadResult> ReadDeadlockEventsAsync(InstanceConfig cfg, DeadlockCursor? cursor, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var ctx = CreateContext(cfg);
            ctx.Session.CommandTimeout = 120; // 全量首扫可能过 10 万行 fn_xe，默认 30s 不够（Chloe 按属性名精确映射，列别名须对齐）
            var result = new DeadlockReadResult
            {
                // GETUTCDATE/GETDATE 全版本兼容（SYSUTCDATETIME 2012+）；分钟粒度足够（进程时间秒级）
                LocalUtcOffset = TimeSpan.FromMinutes(ctx.SqlQuery<int>(
                    "/* dbpilot */ SELECT DATEDIFF(minute, GETUTCDATE(), GETDATE()) AS Value").FirstOrDefault()),
            };

            if (cfg.MajorVersion >= 11)
                ReadFromSystemHealthFile(ctx, cursor, result);
            else
                ReadFromSelfSession(ctx, cfg, cursor, result);
            return result;
        }, ct);

    /// <summary>路径 A（2012+）：system_health event_file 增量读取（文件通配从 target_data 取，offset 游标推进）。</summary>
    private static void ReadFromSystemHealthFile(MsSqlContext ctx, DeadlockCursor? cursor, DeadlockReadResult result)
    {
        var events = ctx.SqlQuery<DeadlockEventRow>(SystemHealthEventSql, new
        {
            session = "system_health",
            initialFile = cursor?.FileName,
            initialOffset = cursor?.Offset,   // initial file/offset 必须成对：无游标时两者都为 NULL（全量首扫）
        }).ToList();
        result.Events = events;

        var cur = ctx.SqlQuery<XeCursorRow>(SystemHealthCursorSql, new
        {
            session = "system_health",
            initialFile = cursor?.FileName,
            initialOffset = cursor?.Offset,
        }).FirstOrDefault();
        // 增量窗口无行时保留旧游标：重置 null 会导致下个 tick 全量重扫（振荡，system_health 全扫每次数秒）
        if (cur is not null)
            result.NewCursor = new DeadlockCursor { FileName = cur.FileName, Offset = cur.FileOffset };
        else
            result.NewCursor = cursor;
    }

    /// <summary>从 @filePattern（含通配）增量读取事件 / 游标的公共片段（路径 A 与 B 共用 fn_xe_file_target_read_file）。
    /// 内联 /* dbpilot */ 标记：多语句批无批首注释位，TopSQL 排除规则按"包含标记"匹配批全文。</summary>
    private const string FnXeHeader = """
        DECLARE @file nvarchar(400);
        SELECT /* dbpilot */ TOP 1 @file = CAST(t.target_data AS xml).value('(/EventFileTarget/File/@name)[1]', 'nvarchar(400)')
        FROM sys.dm_xe_session_targets t
        JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
        WHERE s.name = @session AND t.target_name = 'event_file';
        """;

    /// <summary>路径 A 事件查询：system_health 文件前缀 = target_data 文件名剥两段 _N_N.xel → *.xel 通配。</summary>
    private const string SystemHealthEventSql = FnXeHeader + """
        DECLARE @rev int = CHARINDEX('_', REVERSE(@file));
        SET @file = LEFT(@file, LEN(@file) - @rev);
        SET @rev = CHARINDEX('_', REVERSE(@file));
        SET @file = LEFT(@file, LEN(@file) - @rev);

        IF @file IS NULL
            SELECT CAST(NULL AS nvarchar(max)) AS EventData WHERE 1 = 0;
        ELSE
            SELECT /* dbpilot */ CAST(f.event_data AS nvarchar(max)) AS EventData
            FROM sys.fn_xe_file_target_read_file(@file + N'*.xel', NULL, @initialFile, @initialOffset) f
            WHERE f.object_name = N'xml_deadlock_report'
              AND (@initialFile IS NULL OR NOT (f.file_name = @initialFile AND f.file_offset = @initialOffset)); -- initial_offset 含起点排除游标行；游标 NULL 时 = NULL 谓词恒假，须短路
        """;

    /// <summary>路径 A 游标推进：增量窗口内最大 (文件, offset)（TOP 1 + DESC 引擎流式保最大，免全排序）。</summary>
    private const string SystemHealthCursorSql = FnXeHeader + """
        DECLARE @rev int = CHARINDEX('_', REVERSE(@file));
        SET @file = LEFT(@file, LEN(@file) - @rev);
        SET @rev = CHARINDEX('_', REVERSE(@file));
        SET @file = LEFT(@file, LEN(@file) - @rev);

        IF @file IS NULL
            SELECT CAST(NULL AS nvarchar(400)) AS FileName, CAST(NULL AS bigint) AS FileOffset WHERE 1 = 0;
        ELSE
            SELECT /* dbpilot */ TOP 1 f.file_name AS FileName, f.file_offset AS FileOffset
            FROM sys.fn_xe_file_target_read_file(@file + N'*.xel', NULL, @initialFile, @initialOffset) f
            ORDER BY f.file_offset DESC;
        """;

    /// <summary>自建会话文件目标（路径 B 死锁 / 慢SQL 共用）游标推进 SQL：增量窗口内最大 (文件, offset)。</summary>
    private const string SelfSessionCursorSql = """
        /* dbpilot */
        SELECT TOP 1 f.file_name AS FileName, f.file_offset AS FileOffset
        FROM sys.fn_xe_file_target_read_file(@filePattern, NULL, @initialFile, @initialOffset) f
        ORDER BY f.file_offset DESC;
        """;

    /// <summary>XE 文件通配模式：TrimEnd 剥掉尾部 \ 后必须重新补分隔符（丢分隔符 fn_xe 静默匹配 0 文件）。</summary>
    private static string XeFilePattern(string path, string sessionName)
        => $@"{path.TrimEnd('\\')}\{sessionName}*.xel";

    /// <summary>EXEC 动态 SQL 内的路径字面量：单引号转义防注入 + 保证尾部目录分隔符。</summary>
    private static string SafeXePathLiteral(string path)
    {
        var safe = path.Replace("'", "''");
        return safe.EndsWith(@"\", StringComparison.Ordinal) ? safe : safe + @"\";
    }

    /// <summary>路径 B（2008/2008R2）：幂等创建自建 DBPilot_Deadlock 会话（文件目标，XeFilePath 目录）+ 启动 + 增量读取；
    /// 创建/启动失败（权限等）降级 system_health ring_buffer（timestamp 水位）。</summary>
    private static void ReadFromSelfSession(MsSqlContext ctx, InstanceConfig cfg, DeadlockCursor? cursor, DeadlockReadResult result)
    {
        var path = cfg.XeFilePath?.Trim();
        try
        {
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("XeFilePath 未配置，无法创建自建死锁 XE 会话");

            // 幂等创建 + 启动（2008 目标名 asynchronous_file_target；路径单引号转义防注入）
            var safePath = SafeXePathLiteral(path);
            ctx.SqlQuery<int>($"""
                /* dbpilot */
                IF NOT EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'DBPilot_Deadlock')
                    EXEC(N'CREATE EVENT SESSION [DBPilot_Deadlock] ON SERVER
                        ADD EVENT sqlserver.xml_deadlock_report
                        ADD TARGET package0.asynchronous_file_target(
                            SET filename = N''{safePath}DBPilot_Deadlock.xel'', max_file_size = 20, max_rollover_files = 5)
                        WITH (MAX_DISPATCH_LATENCY = 60 SECONDS);');
                IF NOT EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = N'DBPilot_Deadlock')
                    ALTER EVENT SESSION [DBPilot_Deadlock] ON SERVER STATE = START;
                SELECT 1 AS Value;
                """).FirstOrDefault();

            var filePattern = XeFilePattern(path, "DBPilot_Deadlock");
            result.Events = ctx.SqlQuery<DeadlockEventRow>("""
                /* dbpilot */
                SELECT CAST(f.event_data AS nvarchar(max)) AS EventData
                FROM sys.fn_xe_file_target_read_file(@filePattern, NULL, @initialFile, @initialOffset) f
                WHERE f.object_name = N'xml_deadlock_report'
                  AND (@initialFile IS NULL OR NOT (f.file_name = @initialFile AND f.file_offset = @initialOffset)); -- initial_offset 含起点排除游标行；游标 NULL 短路（= NULL 谓词恒假）
                """, new
            {
                filePattern,
                initialFile = cursor?.FileName,
                initialOffset = cursor?.Offset,
            }).ToList();

            var cur = ctx.SqlQuery<XeCursorRow>(SelfSessionCursorSql, new
            {
                filePattern,
                initialFile = cursor?.FileName,
                initialOffset = cursor?.Offset,
            }).FirstOrDefault();
            if (cur is not null)
                result.NewCursor = new DeadlockCursor { FileName = cur.FileName, Offset = cur.FileOffset };
            else
                result.NewCursor = cursor;   // 增量窗口无行时保留旧游标，防全量重扫振荡
        }
        catch
        {
            // 降级：system_health ring_buffer（timestamp 水位）；创建失败原因不吞（Debug 日志留痕在调用方）
            ReadFromRingBuffer(ctx, cursor, result);
        }
    }

    /// <summary>降级读取：system_health ring_buffer 全量 XML → C# 侧过滤 xml_deadlock_report + timestamp 水位。</summary>
    private static void ReadFromRingBuffer(MsSqlContext ctx, DeadlockCursor? cursor, DeadlockReadResult result)
    {
        var ring = ctx.SqlQuery<string>("""
            /* dbpilot */
            SELECT CAST(t.target_data AS nvarchar(max)) AS Value
            FROM sys.dm_xe_session_targets t
            JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
            WHERE s.name = N'system_health' AND t.target_name = 'ring_buffer';
            """).FirstOrDefault();
        if (string.IsNullOrEmpty(ring)) return;

        var watermark = cursor?.TimestampUtc;
        var maxSeen = watermark;
        foreach (var evt in System.Xml.Linq.XElement.Parse(ring).Descendants("event"))
        {
            if ((string?)evt.Attribute("name") != "xml_deadlock_report") continue;
            var ts = DeadlockReportParser.ParseTimestamp((string?)evt.Attribute("timestamp"));
            if (ts is null) continue;
            if (watermark is not null && ts <= watermark) continue;

            result.Events.Add(new DeadlockEventRow { EventData = evt.ToString(System.Xml.Linq.SaveOptions.DisableFormatting) });
            if (maxSeen is null || ts > maxSeen) maxSeen = ts;
        }
        result.NewCursor = new DeadlockCursor { TimestampUtc = maxSeen };
    }

    /// <summary>fn_xe 游标行。</summary>
    private sealed class XeCursorRow
    {
        public string FileName { get; set; } = "";
        public long FileOffset { get; set; }
    }

    // ---------- 慢SQL采集（版本分支） ----------

    /// <summary>XE duration 字段官方口径为微秒（2008/2012+ 同），与文档"2012+ 毫秒"说法不符 —— 统一按 µs，实测修正。</summary>
    public Task EnsureSlowSqlCaptureAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var path = cfg.XeFilePath?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("XeFilePath 未配置，无法创建慢SQL XE 会话");

            // 版本分支：2008/2008R2 目标名 asynchronous_file_target；2012+ event_file
            var target = cfg.MajorVersion >= 11 ? "event_file" : "asynchronous_file_target";
            var safePath = SafeXePathLiteral(path);
            var thresholdUs = Math.Max(1, cfg.SlowSqlThresholdMs) * 1000L;   // 阈值 ms → µs

            var ctx = CreateContext(cfg);
            ctx.Session.CommandTimeout = 30;
            ctx.SqlQuery<int>($"""
                /* dbpilot */
                IF NOT EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'DBPilot_SlowSql')
                    EXEC(N'CREATE EVENT SESSION [DBPilot_SlowSql] ON SERVER
                        ADD EVENT sqlserver.rpc_completed( SET collect_statement = 1
                            ACTION(sqlserver.database_name, sqlserver.client_app_name, sqlserver.client_hostname,
                                   sqlserver.username, sqlserver.session_id)
                            WHERE duration >= {thresholdUs} AND sqlserver.is_system = 0
                              AND sqlserver.client_app_name <> N''DBPilot''),
                        ADD EVENT sqlserver.sql_batch_completed(
                            ACTION(sqlserver.database_name, sqlserver.client_app_name, sqlserver.client_hostname,
                                   sqlserver.username, sqlserver.session_id)
                            WHERE duration >= {thresholdUs} AND sqlserver.is_system = 0
                              AND sqlserver.client_app_name <> N''DBPilot'')
                        ADD TARGET package0.{target}(
                            SET filename = N''{safePath}DBPilot_SlowSql.xel'', max_file_size = 50, max_rollover_files = 10)
                        WITH (MAX_DISPATCH_LATENCY = 30 SECONDS);');
                IF NOT EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = N'DBPilot_SlowSql')
                    ALTER EVENT SESSION [DBPilot_SlowSql] ON SERVER STATE = START;
                SELECT 1 AS Value;
                """).FirstOrDefault();
        }, ct);

    /// <summary>增量读取自建会话落盘文件（fn_xe，file/offset 成对 NULL 首扫全量、排除游标行 —— 踩坑结论同死锁采集）。</summary>
    public Task<SlowSqlReadResult> PollSlowSqlAsync(InstanceConfig cfg, SlowSqlCursor? cursor, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var path = cfg.XeFilePath?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("XeFilePath 未配置，无法读取慢SQL XE 文件");
            var filePattern = XeFilePattern(path, "DBPilot_SlowSql");

            var ctx = CreateContext(cfg);
            ctx.Session.CommandTimeout = 120;

            var result = new SlowSqlReadResult
            {
                Events = ctx.SqlQuery<SlowSqlEventRow>("""
                    /* dbpilot */
                    SELECT CAST(f.event_data AS nvarchar(max)) AS EventData
                    FROM sys.fn_xe_file_target_read_file(@filePattern, NULL, @initialFile, @initialOffset) f
                    WHERE @initialFile IS NULL OR NOT (f.file_name = @initialFile AND f.file_offset = @initialOffset) -- initial_offset 含起点排除游标行；游标 NULL 短路（= NULL 谓词恒假，首扫全量必读）
                    """, new { filePattern, initialFile = cursor?.FileName, initialOffset = cursor?.Offset }).ToList(),
            };

            var cur = ctx.SqlQuery<XeCursorRow>(SelfSessionCursorSql,
                new { filePattern, initialFile = cursor?.FileName, initialOffset = cursor?.Offset }).FirstOrDefault();
            if (cur is not null)
                result.NewCursor = new SlowSqlCursor { FileName = cur.FileName, Offset = cur.FileOffset };
            else
                result.NewCursor = cursor;   // 增量窗口无行时保留旧游标，防全量重扫振荡
            return result;
        }, ct);


    /// <summary>拼接 Top SQL 全文（③④⑥ 由 filter 动态生成；internal 供单测）。</summary>
    internal static string BuildTopSqlSql(TopSqlFilter filter)
    {
        var dynamicClauses = string.Join("\n                        AND ", BuildTopSqlFilterClauses(filter));

        return $"""
                /* dbpilot */
                SELECT Fingerprint, DbName, SqlText, FullSqlText, ExecutionCount, TotalElapsedUs, TotalWorkerUs,
                       TotalLogicalReads, TotalPhysicalReads, TotalWrites, MaxElapsedUs, LastExecutionTime
                FROM (SELECT ISNULL(LOWER(CONVERT(varchar(32), qs.query_hash, 2)),
                                    LOWER(CONVERT(varchar(64), qs.sql_handle, 2)))          AS Fingerprint,
                             COALESCE(DB_NAME(st.dbid), DB_NAME(pdb.plan_dbid))              AS DbName,
                             MAX(SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
                                 ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                                                                ELSE qs.statement_end_offset END
                                   - qs.statement_start_offset) / 2) + 1))                    AS SqlText,
                             MAX(st.text)                                                      AS FullSqlText,
                             SUM(qs.execution_count)                                            AS ExecutionCount,
                             SUM(qs.total_elapsed_time)                                         AS TotalElapsedUs,
                             SUM(qs.total_worker_time)                                          AS TotalWorkerUs,
                             SUM(qs.total_logical_reads)                                        AS TotalLogicalReads,
                             SUM(qs.total_physical_reads)                                       AS TotalPhysicalReads,
                             SUM(qs.total_logical_writes)                                        AS TotalWrites,
                             MAX(qs.max_elapsed_time)                                            AS MaxElapsedUs,
                             DATEADD(MILLISECOND, MIN(tz.tz_ms), MAX(qs.last_execution_time)) AS LastExecutionTime
                      FROM sys.dm_exec_query_stats qs
                      OUTER APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                      OUTER APPLY (SELECT TOP 1 CAST(pa.value AS int) AS plan_dbid
                                   FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa
                                   WHERE pa.attribute = N'dbid') pdb
                      CROSS APPLY (SELECT DATEDIFF(MILLISECOND, SYSDATETIME(), SYSUTCDATETIME()) AS tz_ms) tz
                      WHERE {dynamicClauses}
                        AND (@db = N'' OR COALESCE(DB_NAME(st.dbid), DB_NAME(pdb.plan_dbid)) = @db)
                      GROUP BY COALESCE(DB_NAME(st.dbid), DB_NAME(pdb.plan_dbid)),
                               ISNULL(LOWER(CONVERT(varchar(32), qs.query_hash, 2)),
                                      LOWER(CONVERT(varchar(64), qs.sql_handle, 2)))) q
                WHERE (@db <> N'' OR q.DbName IS NULL                                                        -- ⑥⑦ 外层为“保留条件”
                       OR (q.DbName NOT IN (N'msdb', N'model')                                         -- ⑥ 系统库内部活动
                           AND ({(filter.ExcludeSystemDb ? 1 : 0)} = 0 OR q.DbName <> N'master')))      -- ⑦ 兜底开关：再排除 master
                """;
    }

    /// <summary>Top SQL 通道共用的噪音排除 WHERE 子句（①~⑤，见 GetTopSqlRealtimeAsync 注释；
    /// 计划快照采集 GetQueryPlanStatsAsync 同口径复用；internal 供单测）。</summary>
    internal static List<string> BuildTopSqlFilterClauses(TopSqlFilter filter)
    {
        var clauses = new List<string>
        {
            "st.text IS NOT NULL",                          // ① 文本不可解析
            "st.text NOT LIKE '/* rds internal mark */%'",   // ② RDS 代理官方标记
            "st.text NOT LIKE '%/* dbpilot */%'",            // ③ 平台自监控 SQL（统一 /* dbpilot */ 标记，防自采集上榜）
        };

        // ④ 特征模式（配置）：单引号转义 + 限量，防注入/防超长 SQL
        foreach (var p in filter.Patterns.Take(MaxPatterns))
        {
            if (!p.IsNullOrWhiteSpace())
                clauses.Add($"st.text NOT LIKE '{p.Trim().Replace("'", "''")}'");
        }

        // ⑤ 指纹黑名单（平台库）：仅接受小写 hex（Provider 侧指纹即此格式），非法值直接忽略
        var fps = filter.Fingerprints.Where(x => !x.IsNullOrWhiteSpace() && FingerprintRegex.IsMatch(x)).Take(MaxFingerprints).ToList();
        if (fps.Count > 0)
            clauses.Add($"""
                ISNULL(LOWER(CONVERT(varchar(32), qs.query_hash, 2)), LOWER(CONVERT(varchar(64), qs.sql_handle, 2)))
                NOT IN ({string.Join(", ", fps.Select(x => $"N'{x}'"))})
                """);

        return clauses;
    }

    /// <summary>
    /// 计划快照采集：dm_exec_query_stats 按 (指纹, plan_hash, plan_handle) 聚合（2008 即有相关列）。
    /// 噪音排除与实时 Top SQL 同口径（①~⑤，⑥⑦库过滤属查询侧语义此处不适用）；
    /// 累计值为微秒（平台侧转 ms）；creation_time 本地时间转 UTC；plan_handle 仅作 XML 抓取寻址键。
    /// </summary>
    public Task<List<QueryPlanRawRow>> GetQueryPlanStatsAsync(InstanceConfig cfg, TopSqlFilter? filter = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var clauses = string.Join("\n                      AND ", BuildTopSqlFilterClauses(filter ?? new TopSqlFilter()));

            var sql = $"""
                /* dbpilot */
                SELECT Fingerprint, DbName, QueryPlanHash, PlanHandleHex, CreationTimeUtc,
                       ExecutionCount, TotalElapsedUs, TotalWorkerUs, TotalLogicalReads,
                       StatementStartOffset, StatementEndOffset
                FROM (SELECT ISNULL(LOWER(CONVERT(varchar(32), qs.query_hash, 2)),
                                    LOWER(CONVERT(varchar(64), qs.sql_handle, 2)))      AS Fingerprint,
                             COALESCE(DB_NAME(st.dbid), DB_NAME(pdb.plan_dbid))          AS DbName,
                             LOWER(CONVERT(varchar(32), qs.query_plan_hash, 2))          AS QueryPlanHash,
                             LOWER(CONVERT(varchar(64), qs.plan_handle, 2))              AS PlanHandleHex,
                             MIN(qs.statement_start_offset)                              AS StatementStartOffset,
                             MIN(qs.statement_end_offset)                                AS StatementEndOffset,
                             DATEADD(MILLISECOND, MIN(tz.tz_ms), MIN(qs.creation_time))  AS CreationTimeUtc,
                             SUM(qs.execution_count)                                     AS ExecutionCount,
                             SUM(qs.total_elapsed_time)                                  AS TotalElapsedUs,
                             SUM(qs.total_worker_time)                                   AS TotalWorkerUs,
                             SUM(qs.total_logical_reads)                                 AS TotalLogicalReads
                      FROM sys.dm_exec_query_stats qs
                      OUTER APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                      OUTER APPLY (SELECT TOP 1 CAST(pa.value AS int) AS plan_dbid
                                   FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa
                                   WHERE pa.attribute = N'dbid') pdb
                      CROSS APPLY (SELECT DATEDIFF(MILLISECOND, SYSDATETIME(), SYSUTCDATETIME()) AS tz_ms) tz
                      WHERE {clauses}
                      GROUP BY COALESCE(DB_NAME(st.dbid), DB_NAME(pdb.plan_dbid)),
                               ISNULL(LOWER(CONVERT(varchar(32), qs.query_hash, 2)),
                                      LOWER(CONVERT(varchar(64), qs.sql_handle, 2))),
                               LOWER(CONVERT(varchar(32), qs.query_plan_hash, 2)),
                               LOWER(CONVERT(varchar(64), qs.plan_handle, 2))) q
                """;

            return CreateContext(cfg).SqlQuery<QueryPlanRawRow>(sql);
        }, ct);

    /// <summary>
    /// 计划抓取（首批新 plan_hash 时调用）：dm_exec_query_plan 整批 XML 优先，query_plan 为 NULL 时
    /// （在缓存但引擎不给 XML，如嵌套超 128 层）兜 dm_exec_text_query_plan 语句级文本计划——文本无
    /// 嵌套上限，实测 NULL 面 100% 可兜住。两者皆空（handle 已驱逐/引擎都不给）或空串不入返回字典
    /// → 落库 NULL（属设计接受项）；空串绝不能入库——hasXml 按 IS NULL 判定会被骗过。
    /// 返回键 = handle 小写 hex + 语句偏移（同 handle 多语句不撞键），仅供采集侧寻址不落库。
    /// </summary>
    public Task<Dictionary<string, string>> GetQueryPlanXmlsAsync(InstanceConfig cfg, List<QueryPlanHandleRef> handles, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var result = new Dictionary<string, string>();
            // 仅接受小写/大写 hex（来源即 Provider CONVERT 产物，防注入兜底），按 50/批 IN 查询
            var valid = handles.Where(x => !x.HandleHex.IsNullOrWhiteSpace() && PlanHandleRegex.IsMatch(x.HandleHex)).ToList();
            if (valid.Count == 0) return result;

            foreach (var batch in valid.Select(x => x.HandleHex).Distinct().Chunk(50))
            {
                foreach (var row in CreateContext(cfg).SqlQuery<PlanXmlRow>(BuildPlanXmlSql(batch)))
                    if (!row.PlanXml.IsNullOrWhiteSpace())
                        result[new QueryPlanHandleRef(row.HandleHex ?? "", row.StartOffset, row.EndOffset).Key] = row.PlanXml;
            }
            return result;
        }, ct);

    /// <summary>计划抓取 SQL（internal 供单测守卫）：单趟双通道，XML 优先、文本兜底。</summary>
    internal static string BuildPlanXmlSql(IEnumerable<string> handleBatch) => $"""
        /* dbpilot */
        SELECT CONVERT(varchar(64), qs.plan_handle, 2)   AS HandleHex,
               qs.statement_start_offset                  AS StartOffset,
               qs.statement_end_offset                    AS EndOffset,
               ISNULL(CONVERT(nvarchar(max), qp.query_plan),
                      CONVERT(nvarchar(max), tp.query_plan)) AS PlanXml
        FROM sys.dm_exec_query_stats qs
        OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
        OUTER APPLY sys.dm_exec_text_query_plan(qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset) tp
        WHERE UPPER(CONVERT(varchar(64), qs.plan_handle, 2)) IN ({string.Join(", ", handleBatch.Select(x => $"UPPER(N'{x}')"))})
        """;

    private class PlanXmlRow
    {
        public string? HandleHex { get; set; }
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string? PlanXml { get; set; }
    }

    // ---------- 实例性能指标采集（趋势页 60s 快照） ----------

    /// <summary>
    /// 实例性能指标快照：① CPU —— RING_BUFFER_SCHEDULER_MONITOR 最新一条 SystemHealth 采样（100 - SystemIdle，
    /// 采样约每 60s 一条，与采集节奏对齐；与 fn_xe 同款坑：XML 谓词查询前先 SET QUOTED_IDENTIFIER ON，
    /// 且 CAST(xml) 只作用于 TOP 1 行避免全环缓冲解析）；② 内存 + 全实例 IO 累计 —— 单行查询
    /// （2008/2008R2 的 dm_os_sys_memory 列名是 *_in_bytes，版本分支 ÷1024 对齐 2012+ 的 *_kb 列）；
    /// ③ 性能计数器 —— 不做 instance_name 过滤（Buffer cache hit ratio 等 base 行 instance 为空，
    /// SQL 端过滤会丢行），一次取回 value/base 两种行由平台侧按 counter_name 配对。
    /// </summary>
    public Task<InstanceMetricsSnapshot> GetInstanceMetricsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var ctx = CreateContext(cfg);
            var snapshot = new InstanceMetricsSnapshot();

            const string cpuSql = """
                SET QUOTED_IDENTIFIER ON;
                /* dbpilot */
                SELECT CAST(100 - CAST(x.record AS xml).value('(Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int') AS decimal(5,2)) AS CpuUsagePct
                FROM (SELECT TOP 1 record
                      FROM sys.dm_os_ring_buffers
                      WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
                      ORDER BY timestamp DESC) x
                """;
            snapshot.CpuUsagePct = ctx.SqlQuery<CpuRow>(cpuSql).FirstOrDefault()?.CpuUsagePct;

            // 2008（< 11）dm_os_sys_memory 为 bytes 列；dm_os_process_memory.physical_memory_in_use_kb 全版本一致
            var (totalCol, availCol) = cfg.MajorVersion >= 11
                ? ("sm.total_physical_memory_kb", "sm.available_physical_memory_kb")
                : ("sm.physical_memory_in_bytes / 1024", "sm.available_physical_memory_in_bytes / 1024");
            var sysSql = $"""
                /* dbpilot */
                SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(32)) AS ProductVersion,
                       {totalCol}   AS OsTotalMemoryKb,
                       {availCol}   AS OsAvailableMemoryKb,
                       pm.physical_memory_in_use_kb AS SqlMemoryKb,
                       vfs.IoReads, vfs.IoWrites, vfs.IoBytesRead, vfs.IoBytesWritten
                FROM (SELECT SUM(num_of_reads)      AS IoReads,
                             SUM(num_of_writes)     AS IoWrites,
                             SUM(num_of_bytes_read) AS IoBytesRead,
                             SUM(num_of_bytes_written) AS IoBytesWritten
                      FROM sys.dm_io_virtual_file_stats(NULL, NULL)) vfs
                CROSS JOIN sys.dm_os_sys_memory sm
                CROSS JOIN sys.dm_os_process_memory pm
                """;
            var sys = ctx.SqlQuery<MetricsSysRow>(sysSql).FirstOrDefault();
            if (sys != null)
            {
                snapshot.ProductVersion = sys.ProductVersion;
                snapshot.OsTotalMemoryKb = sys.OsTotalMemoryKb;
                snapshot.OsAvailableMemoryKb = sys.OsAvailableMemoryKb;
                snapshot.SqlMemoryKb = sys.SqlMemoryKb;
                snapshot.IoReads = sys.IoReads;
                snapshot.IoWrites = sys.IoWrites;
                snapshot.IoBytesRead = sys.IoBytesRead;
                snapshot.IoBytesWritten = sys.IoBytesWritten;
            }

            // 对象名按后缀匹配（命名实例前缀 MSSQL$实例名）；Transactions/sec 取 _Total、Locks 取 _Total。
            // object_name 为 nchar 定长填充尾随空格：= 比较忽略尾空格而 LIKE 不忽略 → 必须 RTRIM 后再 LIKE
            const string countersSql = """
                /* dbpilot */
                SELECT RTRIM(counter_name)  AS CounterName,
                       RTRIM(object_name)   AS ObjectName,
                       RTRIM(instance_name) AS InstanceName,
                       cntr_value           AS CntrValue
                FROM sys.dm_os_performance_counters
                WHERE (RTRIM(object_name) LIKE '%:SQL Statistics'
                       AND counter_name IN (N'Batch Requests/sec', N'SQL Compilations/sec', N'SQL Re-Compilations/sec'))
                   OR (RTRIM(object_name) LIKE '%:Databases' AND instance_name = N'_Total' AND counter_name = N'Transactions/sec')
                   OR (RTRIM(object_name) LIKE '%:Access Methods' AND counter_name = N'Full Scans/sec')
                   OR (RTRIM(object_name) LIKE '%:General Statistics'
                       AND counter_name IN (N'Logins/sec', N'User Connections', N'Processes blocked'))
                   OR (RTRIM(object_name) LIKE '%:Buffer Manager'
                       AND counter_name IN (N'Page life expectancy', N'Buffer cache hit ratio',
                                            N'Buffer cache hit ratio base', N'Lazy Writes/sec'))
                   OR (RTRIM(object_name) LIKE '%:Locks' AND instance_name = N'_Total'
                       AND counter_name IN (N'Number of Deadlocks/sec', N'Lock Timeouts/sec', N'Lock Waits/sec'))
                """;
            snapshot.Counters = ctx.SqlQuery<CounterRow>(countersSql);

            return snapshot;
        }, ct);

    /// <summary>
    /// 磁盘卷用量（每卷一行，master_files CROSS APPLY 去重）：2008（&lt; 10.5）无 dm_os_volume_stats ——
    /// 版本守卫为真才 EXEC 动态 SQL（缺失 DMV 的编译错误发生在批编译期，IF 包裹普通 SELECT 挡不住，
    /// 动态 SQL 不执行不编译），2008 返回空集自然降级、调用方跳过落库。
    /// </summary>
    public Task<List<InstanceDiskRawRow>> GetInstanceDiskUsageAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                DECLARE @ver nvarchar(50) = CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50));
                DECLARE @major int = CAST(PARSENAME(@ver, 4) AS int);
                DECLARE @minor int = ISNULL(CAST(NULLIF(PARSENAME(@ver, 3), '') AS int), 0);
                IF @major > 10 OR (@major = 10 AND @minor >= 50)
                    EXEC(N'
                        /* dbpilot */
                        SELECT vs.volume_mount_point            AS VolumeMountPoint,
                               CAST(vs.total_bytes / 1048576 AS bigint)     AS TotalMb,
                               CAST(vs.available_bytes / 1048576 AS bigint) AS AvailableMb
                        FROM sys.master_files f
                        CROSS APPLY sys.dm_os_volume_stats(f.database_id, f.file_id) vs
                        GROUP BY vs.volume_mount_point, vs.total_bytes, vs.available_bytes');
                """;

            return CreateContext(cfg).SqlQuery<InstanceDiskRawRow>(sql);
        }, ct);

    /// <summary>CPU 采样行（GetInstanceMetricsAsync 内部用）。</summary>
    private sealed class CpuRow
    {
        public decimal? CpuUsagePct { get; set; }
    }

    /// <summary>内存/IO 单行查询结果（GetInstanceMetricsAsync 内部用）。</summary>
    private sealed class MetricsSysRow
    {
        public string? ProductVersion { get; set; }
        public long? OsTotalMemoryKb { get; set; }
        public long? OsAvailableMemoryKb { get; set; }
        public long? SqlMemoryKb { get; set; }
        public long? IoReads { get; set; }
        public long? IoWrites { get; set; }
        public long? IoBytesRead { get; set; }
        public long? IoBytesWritten { get; set; }
    }

    private const int MaxPatterns = 100;
    private const int MaxFingerprints = 1000;

    /// <summary>指纹格式：query_hash 16 位 / sql_handle 32~64 位小写 hex。</summary>
    private static readonly Regex FingerprintRegex = new("^[0-9a-f]{16}$|^[0-9a-f]{32,64}$", RegexOptions.Compiled);

    /// <summary>plan_handle hex 格式（64 位小写/大写）。</summary>
    private static readonly Regex PlanHandleRegex = new("^[0-9a-fA-F]{16,128}$", RegexOptions.Compiled);

    /// <summary>实例连接按配置动态建上下文（每实例独立连接串，用后即弃）；initialCatalog 指定目标库（按需诊断逐库执行）。</summary>
    private static MsSqlContext CreateContext(InstanceConfig cfg, string? initialCatalog = null)
        => new(BuildConnectionString(cfg, initialCatalog));

    /// <summary>构建实例连接串：Encrypt=False + TrustServerCertificate=True（内部网络 + 2008 兼容），
    /// ApplicationName=DBPilot 供 XE 会话/排除规则识别平台自监控连接。</summary>
    internal static string BuildConnectionString(InstanceConfig cfg, string? initialCatalog = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = cfg.Port == 1433 ? cfg.Host : $"{cfg.Host},{cfg.Port}",
            UserID = cfg.LoginName,
            Password = cfg.Password,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            ApplicationName = "DBPilot"
        };
        if (!initialCatalog.IsNullOrEmpty())
            builder.InitialCatalog = initialCatalog;
        return builder.ConnectionString;
    }

    /// <summary>“10.50.x”→105（2008 R2），其余取主版本（11.x→11）。</summary>
    public static int ParseMajorVersion(string productVersion)
    {
        if (productVersion.IsNullOrEmpty())
            return 0;

        var segments = productVersion.Split('.');
        if (!int.TryParse(segments[0], out var major))
            return 0;

        if (segments.Length > 1 && int.TryParse(segments[1], out var minor) && minor > 0)
        {
            // 10.5 → 105；10.50（ProductVersion 两位小数写法）同样 → 105
            var minorDigit = minor >= 10 ? minor / 10 : minor;
            return major * 10 + minorDigit;
        }

        return major;
    }

    /// <summary>SERVERPROPERTY('ErrorLogFileName')（如 E:\SQLDATA\MSSQL\LOG\ERRORLOG）剥末段文件名得日志目录；
    /// 无分隔符/空值返回 null（受限环境该属性可为 NULL，探测失败由调用方走 xe_file_path 手动值）。</summary>
    internal static string? DeriveLogDirectory(string? errorLogFileName)
    {
        var p = errorLogFileName?.Trim();
        if (string.IsNullOrEmpty(p)) return null;
        var idx = p.LastIndexOf('\\');
        return idx <= 0 ? null : p[..idx];
    }

    /// <summary>版本探测结果行（列别名与属性名对齐，Chloe 按名映射）。</summary>
    private sealed class ProbeRow
    {
        public string? ProductVersion { get; set; }
        public string? Edition { get; set; }
        public string? MachineName { get; set; }
        public int CpuCores { get; set; }
        public DateTime UtcNow { get; set; }
        public string? ErrorLogFileName { get; set; }
        public DateTime? InstanceStartTime { get; set; }
    }
}
