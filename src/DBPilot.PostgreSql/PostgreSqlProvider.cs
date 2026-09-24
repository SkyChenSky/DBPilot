using System.Data;
using System.Text.RegularExpressions;
using Chloe.Infrastructure;
using Chloe.PostgreSQL;
using DBPilot.Common;
using DBPilot.Core.Blocking;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.Providers;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.TopSql;
using IDatabaseProvider = DBPilot.Core.Providers.IDatabaseProvider;   // Chloe.Infrastructure 同名接口消歧
using Npgsql;

namespace DBPilot.PostgreSql;

/// <summary>
/// PostgreSQL Provider（PG 13+，开发基准 18.4 RDS 实测）。
/// 数据访问与 SS/My Provider 同构：Chloe SqlQuery 原生 SQL（同步执行，Task.Run 包装），
/// 每实例按配置动态建 PgSQLContext。时间列 timestamptz 往返即 UTC（Npgsql Kind=Utc，探针实证），
/// 无 MySQL 侧本地时间 TIMESTAMPDIFF 换算负担。
/// 指标口径：pg_stat_database 聚合映射成 SQL Server 计数器名——QPS 语义是<b>事务域</b>
/// （xact_commit+xact_rollback，与 SS Batch Requests/sec 语句域不同，文档注明）；
/// OS 级 CPU / 内存 / PLE / 编译计数无对等项留 null，前端按能力矩阵隐藏。
/// 会话/阻塞：pg_stat_activity（application_name='DBPilot' 识别自监控连接——PG 连接属性通用通道，
/// 优于 MySQL 语句特征兜底）+ pg_blocking_pids 阻塞边 + pg_locks 对象级锁（regclass 列须 ::text 取）。
/// 能力边界：死锁事件 / 计划快照 / 缺失索引 / 碎片 / 慢SQL事件通道无对等数据源抛
/// <see cref="DbpilotUnsupportedException"/>——慢SQL页降级读 top_sql_delta 模板榜（查询侧，采集不跑）、
/// 死锁页降级趋势-only（deadlocks 计数器 → dbpilot_instance_metrics 聚合）。
/// </summary>
[DbpilotEngine(DbpilotEngines.PostgreSql, UnsupportedFeatures = new string[]
{
    DbpilotFeatures.DeadlockEvents,
    DbpilotFeatures.QueryPlanSnapshot,
    DbpilotFeatures.MissingIndex,
    DbpilotFeatures.Fragmentation,
    DbpilotFeatures.SlowSqlXeChannel,
})]
[DbpilotCapabilities(None = new string[]
{
    DbpilotCapabilityKeys.DeadlockEvents,
    DbpilotCapabilityKeys.QueryPlanSnapshot,
    DbpilotCapabilityKeys.MissingIndex,
    DbpilotCapabilityKeys.Fragmentation,
    DbpilotCapabilityKeys.SlowSqlEvents,
    DbpilotCapabilityKeys.OsCpuMem,
    DbpilotCapabilityKeys.Ple,
    DbpilotCapabilityKeys.CompileStats,
    DbpilotCapabilityKeys.BlockedProcesses,
    DbpilotCapabilityKeys.IndexDisableScript,
})]
public partial class PostgreSqlProvider : IDatabaseProvider
{
    /// <summary>
    /// 连接测试：SELECT 1 建连并测延迟，随后逐项自检环境（缺失归入清单，带影响与修复指引）。
    /// pg_stat_statements 已 preload 未安装时直接代装（幂等，连接库装一次统计视图全局可见）。
    /// </summary>
    public Task<ConnectionTestResult> TestConnectionAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new ConnectionTestResult();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var ctx = CreateContext(cfg);
                ctx.SqlQuery<int>("/* dbpilot */ SELECT 1 AS Value");
                watch.Stop();
                result.Ok = true;
                result.LatencyMs = (int)watch.ElapsedMilliseconds;

                void Add(string permission, string impact, string fix) =>
                    result.MissingPermissions.Add(new MissingPermission { Permission = permission, Impact = impact, FixScript = fix });

                // ① pg_stat_statements（Top SQL 榜 / 慢SQL模板榜数据源）：preload 是启动期参数，
                //    扩展按库安装——代装（sky 类 rds 超管账号可装，幂等无副作用），失败归入缺失清单
                var ext = ctx.SqlQuery<ExtRow>("""
                    /* dbpilot */
                    SELECT current_setting('shared_preload_libraries') AS Preload,
                           (SELECT extversion FROM pg_extension WHERE extname = 'pg_stat_statements') AS ExtVersion
                    """).FirstOrDefault();
                var preloadOk = (ext?.Preload ?? "").Contains("pg_stat_statements", StringComparison.OrdinalIgnoreCase);
                if (!preloadOk)
                {
                    Add("shared_preload_libraries 含 pg_stat_statements",
                        "Top SQL 榜与慢SQL模板榜不可用（扩展未预载，无法安装）",
                        "RDS 控制台参数组 shared_preload_libraries 追加 pg_stat_statements 并重启实例；自建实例 postgresql.conf 同名参数后重启（启动期参数，ALTER SYSTEM 重启同样生效）");
                }
                else if (ext is not null && ext.ExtVersion.IsNullOrEmpty())
                {
                    try { ctx.SqlQuery<int>("/* dbpilot */ CREATE EXTENSION IF NOT EXISTS pg_stat_statements"); }
                    catch (Exception ex)
                    {
                        Add("CREATE EXTENSION pg_stat_statements",
                            $"Top SQL 榜与慢SQL模板榜不可用（扩展未安装且当前账号无权代装）：{ex.GetDeepestException().Message.Sub(200)}",
                            "由 DBA 在连接库执行 CREATE EXTENSION pg_stat_statements;（统计视图装一次全局可见，监控账号只读即可）");
                    }
                }

                // ② pg_stat_statements.track（RDS 常见陷阱：参数组批量置 none——查询侧静默空榜而非报错）
                result.MissingPermissions.AddRange(EvaluatePgssTrack(
                    preloadOk ? QuerySetting(ctx, "pg_stat_statements.track") : null));

                // ③ 会话/阻塞数据源可读性（pg_stat_activity / pg_locks 权限面）
                try { ctx.SqlQuery<int>("/* dbpilot */ SELECT 1 AS Value FROM pg_stat_activity LIMIT 1"); }
                catch (Exception ex)
                {
                    Add("pg_stat_activity 可读",
                        $"会话采样 / 实时阻塞不可用：{ex.GetDeepestException().Message.Sub(200)}",
                        "RDS 加入 pg_rds_superuser 角色或 GRANT pg_monitor TO <账号>;（自建实例由 superuser 授予）");
                }

                // ④ 监控账号 CONNECT 权限面（索引使用率按库连接采集，缺 CONNECT 的库不出数据）
                var noConnect = ctx.SqlQuery<string>("""
                    /* dbpilot */
                    SELECT d.datname
                    FROM pg_database d
                    WHERE d.datallowconn AND NOT d.datistemplate
                      AND NOT has_database_privilege(current_user, d.oid, 'CONNECT')
                    ORDER BY d.datname
                    """).ToList();
                result.MissingPermissions.AddRange(EvaluateConnectDbs(noConnect));
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

    /// <summary>读单个 GUC（missing_ok：未预载时返回 null 而非抛错）。</summary>
    private static string? QuerySetting(PostgreSQLContext ctx, string name)
        => ctx.SqlQuery<string>($"/* dbpilot */ SELECT current_setting('{name}', true) AS Value").FirstOrDefault();

    /// <summary>pg_stat_statements.track 自检（纯函数，供单测）：none → 空榜症状 + RDS 参数组指引
    /// （sky 类账号会话级 SET 被 42501 拒，参数组是唯一通道）；其他取值（top/all）放行。</summary>
    internal static List<MissingPermission> EvaluatePgssTrack(string? track)
    {
        if (track is null || !track.Equals("none", StringComparison.OrdinalIgnoreCase))
            return [];
        return
        [
            new MissingPermission
            {
                Permission = "pg_stat_statements.track = top（当前 none）",
                Impact = "Top SQL 榜与慢SQL模板榜恒空（语句不进统计视图，静默无报错）",
                FixScript = "RDS 控制台参数组修改 pg_stat_statements.track=top 并应用（只读参数，会话级 SET 被拒）；自建实例 ALTER SYSTEM SET pg_stat_statements.track = 'top'; 后 reload",
            },
        ];
    }

    /// <summary>CONNECT 权限自检（纯函数，供单测）：缺连接权限的库逐个列出（索引使用率按库连接）。</summary>
    internal static List<MissingPermission> EvaluateConnectDbs(List<string> noConnectDbs)
        => noConnectDbs.Count == 0 ? []
        :
        [
            new MissingPermission
            {
                Permission = $"CONNECT ON DATABASE {string.Join(", ", noConnectDbs)}",
                Impact = $"索引使用率快照不含这 {noConnectDbs.Count} 个库（按库连接采集，缺 CONNECT 无数据）",
                FixScript = $"GRANT CONNECT ON DATABASE {string.Join(", ", noConnectDbs)} TO <账号>;（库级权限， 由 DBA 执行）",
            },
        ];

    /// <summary>版本/环境探测：version()/server_version_num/inet_server_addr + pg_postmaster_start_time（重启检测），CpuCores 无对等恒 0。</summary>
    public Task<InstanceMeta> ProbeAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT version()                                                    AS ProductVersion,
                       current_setting('server_version_num')                       AS VersionNum,
                       COALESCE(inet_server_addr()::text, 'local socket')           AS MachineName,
                       pg_postmaster_start_time()                                   AS StartUtc,
                       (now() AT TIME ZONE 'utc')                                   AS UtcNow
                """;

            var row = CreateContext(cfg).SqlQuery<ProbeRow>(sql).FirstOrDefault()
                      ?? throw new InvalidOperationException("版本探测无返回行");

            var utcNow = DateTime.SpecifyKind(row.UtcNow, DateTimeKind.Utc);
            return new InstanceMeta
            {
                ProductVersion = row.ProductVersion ?? "",
                Edition = "PostgreSQL",
                MachineName = row.MachineName,
                CpuCores = 0,
                SqlServerStartTimeUtc = DateTime.SpecifyKind(row.StartUtc, DateTimeKind.Utc),
                ClockSkewSeconds = (int)Math.Abs((utcNow - DateTime.UtcNow).TotalSeconds),
                MajorVersion = ParsePostgreSqlVersion(row.VersionNum),
            };
        }, ct);

    /// <summary>"180004"（server_version_num）→ 18；解析失败返回 0。</summary>
    public static int ParsePostgreSqlVersion(string? versionNum)
        => int.TryParse(versionNum, out var n) ? n / 10000 : 0;

    /// <summary>库列表：可连接的非模板库，排除维护库 postgres（等价 MySQL 排 mysql/sys 口径）。</summary>
    public Task<List<string>> GetDatabasesAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT d.datname AS Value
                FROM pg_database d
                WHERE d.datallowconn AND NOT d.datistemplate
                  AND d.datname NOT IN ('postgres', 'template0', 'template1')
                ORDER BY d.datname
                """;

            return CreateContext(cfg).SqlQuery<string>(sql);
        }, ct);

    // ---- 实例指标（pg_stat_database 全库聚合 → SQL Server 计数器名映射） ----

    /// <summary>实例指标快照：pg_stat_database 全库 SUM 交 <see cref="BuildMetricsSnapshot"/> 映射成
    /// SQL Server 计数器名，平台侧 ResolveCounters 零改动消费（value/base 配对 / 差值分类通用）。
    /// Full Scans 口径 = tup_returned − tup_fetched 差值（库级无 seq_scan 列，差值近似顺序扫行量）。</summary>
    public Task<InstanceMetricsSnapshot> GetInstanceMetricsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT (SELECT version())                                AS ProductVersion,
                       COALESCE(SUM(x.xact_commit + x.xact_rollback), 0) AS Xacts,
                       COALESCE(SUM(x.blks_hit), 0)                      AS BlksHit,
                       COALESCE(SUM(x.blks_read), 0)                     AS BlksRead,
                       COALESCE(SUM(x.deadlocks), 0)                     AS Deadlocks,
                       GREATEST(COALESCE(SUM(x.tup_returned), 0) - COALESCE(SUM(x.tup_fetched), 0), 0) AS SeqScan,
                       COALESCE(SUM(x.numbackends), 0)                    AS NumBackends
                FROM pg_stat_database x
                """;

            var row = CreateContext(cfg).SqlQuery<MetricsRawRow>(sql).First();
            return BuildMetricsSnapshot(row.ProductVersion, row.Xacts, row.BlksHit, row.BlksRead,
                row.Deadlocks, row.SeqScan, row.NumBackends);
        }, ct);

    /// <summary>
    /// 聚合行 → 指标快照（纯函数，供单测）：计数器名对齐 ResolveCounters 白名单。
    /// QPS 口径是<b>事务域</b>（xact_commit+xact_rollback）——与 SQL Server Batch Requests/sec
    /// 的语句域不同（PG 无每语句计数器，事务数是最接近的负载口径）；CPU/PLE/编译/OS 内存无对等留 null。
    /// IO：blks_read 是缓冲管理器层读miss 累计（近似物理读 IOPS）；吞吐字节无对等恒 null。
    /// </summary>
    internal static InstanceMetricsSnapshot BuildMetricsSnapshot(string? productVersion,
        long xacts, long blksHit, long blksRead, long deadlocks, long seqScan, long numBackends)
    {
        var snapshot = new InstanceMetricsSnapshot
        {
            ProductVersion = productVersion,
            IoReads = blksRead > 0 ? blksRead : null,
            Counters = [],
        };

        void Add(string counterName, long value)
            => snapshot.Counters.Add(new CounterRow { CounterName = counterName, ObjectName = "PostgreSQL Stats", CntrValue = value });

        Add("Batch Requests/sec", xacts);          // 事务域口径（见方法注释）
        Add("Transactions/sec", xacts);
        Add("Full Scans/sec", seqScan);
        Add("Number of Deadlocks/sec", deadlocks); // 死锁页趋势-only 降级形态的数据源
        Add("User Connections", numBackends);

        // 命中率配对（平台侧 value/base×100）：value = blks_hit，base = hit+read
        if (blksHit + blksRead > 0)
        {
            Add("Buffer cache hit ratio", blksHit);
            Add("Buffer cache hit ratio base", blksHit + blksRead);
        }

        return snapshot;
    }

    /// <summary>
    /// 磁盘用量（库级聚合，无卷级对等数据源）：VolumeMountPoint 列承载库名，AvailableMb 恒 null
    /// （无宿主卷可用空间通道），前端按库一线渲染并注明口径——与 MySQL 同构。
    /// </summary>
    public Task<List<InstanceDiskRawRow>> GetInstanceDiskUsageAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT d.datname                            AS VolumeMountPoint,
                       (pg_database_size(d.oid) / 1048576)  AS TotalMb,
                       NULL::bigint                         AS AvailableMb
                FROM pg_database d
                WHERE d.datallowconn AND NOT d.datistemplate
                ORDER BY d.datname
                """;

            return CreateContext(cfg).SqlQuery<InstanceDiskRawRow>(sql);
        }, ct);

    // ---- 会话 / 阻塞（pg_stat_activity + pg_blocking_pids + pg_locks） ----

    /// <summary>
    /// 活动请求采样（PG 口径）：pg_stat_activity 取 state='active' 的 client backend（睡着头走
    /// <see cref="GetHeadBlockersAsync"/> 补查）。阻塞边 pg_blocking_pids（数组取头；持锁者已断的
    /// 孤儿预备事务映射 -2 系统节点，对齐 MySQL data_lock_waits 口径）。
    /// 噪音排除：① 自身连接（pg_backend_pid）+ application_name='DBPilot'（PG 连接属性通道通用，
    /// Npgsql 连接串 Application Name 透传，RDS 实测可见——优于 MySQL 语句特征兜底）；
    /// ② 语句特征 `/* dbpilot */` 内联标记 + 双引号 "dbpilot_ 表引用（(PG,PG) 形态平台库建在
    /// 被监控同机时 Chloe 落库语句的签名——Chloe.PostgreSQL 双引号标识符，等价 MySQL 反引号口径）；
    /// ③ RDS 内部账号 aurora/rdsadmin + '/* rds internal mark */' 官方自留标记（探针实证存在）。
    /// 等待分桶：wait_event_type='Lock' → 'Waiting for … lock'（WaitBuckets 归 lock 桶）、
    /// 其余等待 → 'Waiting for …'（other 桶）、无等待 → NULL（cpu 桶）——与 MySQL 合成词汇同构。
    /// QueryHash 无对等留 null（采样侧文本指纹兜底）。
    /// </summary>
    public Task<List<ActiveRequestRow>> GetActiveRequestsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() => CreateContext(cfg).SqlQuery<ActiveRequestRow>(ActiveRequestsSql), ct);

    /// <summary>主查询（常量供单测断言）：单行每会话，无 JOIN 无行放大。</summary>
    internal const string ActiveRequestsSql = """
        /* dbpilot */
        SELECT a.pid                                                                        AS SessionId,
               CASE WHEN a.wait_event_type IS NOT NULL THEN 'suspended' ELSE 'running' END  AS Status,
               'Query'                                                                      AS Command,
               a.query_start                                                                AS StartTimeUtc,
               CASE WHEN a.wait_event_type IS NULL THEN NULL
                    WHEN a.wait_event_type = 'Lock' THEN 'Waiting for ' || a.wait_event || ' lock'
                    ELSE 'Waiting for ' || a.wait_event END                                 AS WaitType,
               CASE WHEN a.query_start IS NULL THEN 0
                    ELSE (EXTRACT(EPOCH FROM (clock_timestamp() - a.query_start)) * 1000)::bigint END AS WaitTimeMs,
               COALESCE((SELECT b.pid FROM unnest(pg_blocking_pids(a.pid)) AS b(pid) LIMIT 1),
                        CASE WHEN a.wait_event_type = 'Lock' THEN -2 ELSE 0 END)          AS BlockingSessionId,
               CASE WHEN a.query_start IS NULL THEN 0
                    ELSE (EXTRACT(EPOCH FROM (clock_timestamp() - a.query_start)) * 1000)::bigint END AS TotalElapsedMs,
               CASE WHEN a.xact_start IS NULL THEN 0 ELSE 1 END                             AS OpenTranCount,
               a.usename                                                                    AS LoginName,
               COALESCE(a.client_hostname, a.client_addr::text)                             AS HostName,
               NULLIF(a.application_name, '')                                               AS ProgramName,
               a.datname                                                                    AS DbName,
               a.query                                                                      AS SqlText,
               a.query                                                                      AS BatchSqlText
        FROM pg_stat_activity a
        WHERE a.backend_type = 'client backend'
          AND a.state = 'active'
          AND a.pid <> pg_backend_pid()
          AND a.application_name <> 'DBPilot'
          AND COALESCE(a.query, '') NOT LIKE '%/* dbpilot */%'
          AND COALESCE(a.query, '') NOT LIKE '%"dbpilot_%'
          AND a.usename NOT IN ('aurora', 'rdsadmin')
          AND COALESCE(a.query, '') NOT LIKE '/* rds internal mark */%'
        """;

    /// <summary>
    /// 头阻塞者补查（PG 口径）：主查询只回 active 会话，"睡着拿锁"头 = state='idle in transaction'
    /// （事务开着、锁拿着、无当前语句）。PG 对 idle 会话 query 字段保留最后执行语句（比 MySQL
    /// events_statements_history 补查干净）；xact_start 天然 timestamptz 免时区换算。
    /// </summary>
    public Task<List<HeadBlockerRow>> GetHeadBlockersAsync(InstanceConfig cfg, List<int> headSessionIds, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var sql = $"""
                /* dbpilot */
                SELECT a.pid                                  AS SessionId,
                       a.usename                              AS LoginName,
                       COALESCE(a.client_hostname, a.client_addr::text) AS HostName,
                       NULLIF(a.application_name, '')         AS ProgramName,
                       a.datname                              AS DbName,
                       CASE WHEN a.xact_start IS NULL THEN 0 ELSE 1 END AS OpenTranCount,
                       a.xact_start                           AS TransactionBeginUtc,
                       a.query                                AS LastSqlText
                FROM pg_stat_activity a
                WHERE a.pid IN ({string.Join(", ", headSessionIds)})
                """;

            return CreateContext(cfg).SqlQuery<HeadBlockerRow>(sql);
        }, ct);

    /// <summary>
    /// 阻塞原因（锁资源，PG 口径）：pg_locks 对象级锁（relation/tuple 两类）。
    /// pg_locks.database 是 oid → pg_database 反查库名；relation oid 的 regclass 反查只在
    /// 所属库的 catalog 内可解（本查询连 postgres 库，跨库 cast 落空）——按锁所属库逐库连接
    /// pg_class 反查表名，未命中回落 oid 数字；EntityId 恒 0（对齐 MySQL data_locks 口径）；
    /// granted bool → GRANT/WAIT 映射。
    /// </summary>
    public Task<List<SessionLockRow>> GetSessionLocksAsync(InstanceConfig cfg, List<int> sessionIds, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var sql = $"""
                /* dbpilot */
                SELECT l.pid                   AS SessionId,
                       l.locktype              AS ResourceType,
                       d.datname               AS DbName,
                       l.relation::bigint      AS RelationOid,
                       l.mode                  AS LockMode,
                       CASE WHEN l.granted THEN 'GRANT' ELSE 'WAIT' END AS LockStatus
                FROM pg_locks l
                LEFT JOIN pg_database d ON d.oid = l.database
                WHERE l.pid IN ({string.Join(", ", sessionIds)})
                  AND l.locktype IN ('relation', 'tuple')
                """;

            var rows = CreateContext(cfg).SqlQuery<LockRawRow>(sql);

            // relation oid → 表名：pg_class 是每库独立的 catalog，须连到锁所属库反查（oids 为库内部 long，内联安全）
            var oidNames = new Dictionary<long, string>();
            foreach (var g in rows.Where(r => r.RelationOid is > 0 && !r.DbName.IsNullOrEmpty()).GroupBy(r => r.DbName))
            {
                try
                {
                    var oids = string.Join(", ", g.Select(r => r.RelationOid).Distinct());
                    var nameRows = CreateContext(cfg, g.Key)
                        .SqlQuery<RelationNameRow>($"/* dbpilot */ SELECT c.oid::bigint AS Oid, c.relname AS Name FROM pg_class c WHERE c.oid IN ({oids})");
                    foreach (var n in nameRows)
                        oidNames[n.Oid] = n.Name;
                }
                catch
                {
                    // 缺 CONNECT 的库跳过（对象名回落 oid 数字）
                }
            }

            return rows.Select(r => new SessionLockRow
            {
                SessionId = r.SessionId,
                ResourceType = r.ResourceType,
                DbName = r.DbName,
                LockMode = r.LockMode,
                LockStatus = r.LockStatus,
                ObjectName = r.RelationOid is > 0
                    ? oidNames.TryGetValue(r.RelationOid.Value, out var name) ? name : $"oid:{r.RelationOid}"
                    : null,
            }).ToList();
        }, ct);

    /// <summary>锁资源原始行（Chloe 映射；relation oid 携带回 C# 侧逐库反查）。</summary>
    private sealed class LockRawRow
    {
        public int SessionId { get; set; }
        public string ResourceType { get; set; } = "";
        public string? DbName { get; set; }
        public long? RelationOid { get; set; }
        public string LockMode { get; set; } = "";
        public string LockStatus { get; set; } = "";
    }

    /// <summary>oid → 表名反查行。</summary>
    private sealed class RelationNameRow
    {
        public long Oid { get; set; }
        public string Name { get; set; } = "";
    }

    // ---- Top SQL（pg_stat_statements，queryid 天然指纹） ----

    /// <summary>
    /// Top SQL（PG 口径）：pg_stat_statements —— to_hex(queryid) 即归一化指纹（小写 hex，与黑名单
    /// 页面"排除"按钮存储格式一致）、query 归一化模板（常量已替换，SqlText 的理想语句源；无真实样例
    /// 列，FullSqlText 同值）；计时列 total_exec_time/max_exec_time 是 <b>毫秒</b> double（×1000 → µs，
    /// 平台侧再转 ms）；无 CPU 计时恒 0。行数口径：shared_blks_hit+read 近似逻辑读、shared_blks_read
    /// 近似物理读、shared+local_blks_written 近似写（块口径，与 SS 页/MySQL 行口径都不同，近似展示）。
    /// LastExecutionTime 无对等恒 null（pgs 1.12 无 last exec 列，实时页该列显示 '-'）。
    /// 噪音排除：① '%/* dbpilot */%' 内联标记 + 双引号 "dbpilot_ 表引用（(PG,PG) 形态 Chloe 落库签名，
    /// 与 DefaultPatterns 的 '%\"dbpilot_%' 配置模式双保险——SQL 内联版对未配置部署兜底）；
    /// ② '/* rds internal mark */' RDS 官方自留前缀（探针实证 aurora 会话带此标记）；
    /// ③ 内部账号（aurora/rdsadmin 的 userid 关联排除）；④ 指纹黑名单 + filter.Patterns 配置模式
    /// （T-SQL 特征在 PG LIKE 语义下永不命中为无害 no-op，与 MySQL 同口径）。
    /// 防御：扩展未装（连接库无 pg_stat_statements 视图）→ 空集优雅降级而非报错（TestConnection
    /// 已代装/指引；track=none 时视图 0 行同为此口径）。
    /// </summary>
    public Task<List<TopSqlRawRow>> GetTopSqlRealtimeAsync(InstanceConfig cfg, string db, TopSqlFilter? filter = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var ctx = CreateContext(cfg);
            var hasExt = ctx.SqlQuery<int>("/* dbpilot */ SELECT count(*) AS Value FROM pg_extension WHERE extname = 'pg_stat_statements'")
                .FirstOrDefault() > 0;
            if (!hasExt) return [];

            return ctx.SqlQuery<TopSqlRawRow>(BuildTopSqlSql(filter ?? new TopSqlFilter()), new { db });
        }, ct);

    /// <summary>Top SQL 全文（internal 供单测）：@db 走参数，黑名单/模式/开关 SQL 内联。</summary>
    internal static string BuildTopSqlSql(TopSqlFilter filter)
    {
        // 指纹黑名单：to_hex(queryid) 是小写 hex（≤16 位），仅接受小写 hex（与页面排除按钮存储格式一致）
        var fps = filter.Fingerprints
            .Where(x => !x.IsNullOrWhiteSpace() && Hex64Regex().IsMatch(x))
            .Take(MaxFingerprints)
            .ToList();
        var fpClause = fps.Count > 0
            ? $"\n                  AND to_hex(s.queryid) NOT IN ({string.Join(", ", fps.Select(x => $"'{x}'"))})"
            : "";

        // 特征模式（配置，单引号转义 + 限量，与 SS/My Provider 同口径）：作用于归一化模板 query
        var patternClause = string.Join("", filter.Patterns
            .Where(x => !x.IsNullOrWhiteSpace())
            .Take(MaxPatterns)
            .Select(x => $"\n                  AND s.query NOT LIKE '{x.Trim().Replace("'", "''")}'"));

        return $"""
            /* dbpilot */
            SELECT Fingerprint, DbName, SqlText, FullSqlText, ExecutionCount, TotalElapsedUs, TotalWorkerUs,
                   TotalLogicalReads, TotalPhysicalReads, TotalWrites, MaxElapsedUs, LastExecutionTime
            FROM (
                SELECT to_hex(s.queryid)                                AS Fingerprint,
                       d.datname                                        AS DbName,
                       s.query                                          AS SqlText,
                       s.query                                          AS FullSqlText,
                       s.calls                                          AS ExecutionCount,
                       (s.total_exec_time * 1000)::bigint               AS TotalElapsedUs,
                       0::bigint                                        AS TotalWorkerUs,
                       s.shared_blks_hit + s.shared_blks_read           AS TotalLogicalReads,
                       s.shared_blks_read                               AS TotalPhysicalReads,
                       s.shared_blks_written + s.local_blks_written     AS TotalWrites,
                       (s.max_exec_time * 1000)::bigint                 AS MaxElapsedUs,
                       NULL::timestamptz                                AS LastExecutionTime
                FROM pg_stat_statements s
                JOIN pg_database d ON d.oid = s.dbid
                WHERE s.query IS NOT NULL
                  AND s.query NOT LIKE '%/* dbpilot */%'
                  AND s.query NOT LIKE '%"dbpilot_%'
                  AND s.query NOT LIKE '/* rds internal mark */%'
                  AND s.userid NOT IN (SELECT r.oid FROM pg_roles r WHERE r.rolname IN ('aurora', 'rdsadmin')){fpClause}{patternClause}
                  AND (@db = '' OR d.datname = @db)
            ) q
            WHERE @db <> ''
               OR q.DbName IS NULL
               OR (q.DbName NOT IN ('postgres', 'template0', 'template1')
                   AND ({(filter.ExcludeSystemDb ? 1 : 0)} = 0 OR q.DbName <> 'postgres'))
            """;
    }

    /// <summary>小写 hex（≤64 位）校验；<see cref="GeneratedRegexAttribute"/> 编译期生成。</summary>
    [GeneratedRegex("^[0-9a-f]{1,64}$")]
    private static partial Regex Hex64Regex();

    private const int MaxFingerprints = 1000;
    private const int MaxPatterns = 100;

    // ---- 索引使用率（pg_stat_user_indexes + pg_index，按库连接采集） ----

    /// <summary>
    /// 索引使用率（PG 口径）：pg_stat_user_indexes 每索引一行（含从未使用行——等价 SS 版 sys.indexes
    /// LEFT JOIN 修复）。PG 的统计视图按库隔离（跨库不可查），连接切到目标库（initialCatalog）。
    /// 口径：① idx_scan 无法区分 seek/scan → 全记 UserSeeks（与 MySQL 同）；② 写计数从
    /// pg_stat_user_tables 取表级 n_tup_ins+upd+del（PG 的索引使用率视图无写维护计数——但
    /// idx_scan=0 且表有写即<b>真实未使用</b>，无 MySQL COUNT_FETCH 被写维护抬高的坑，IsUnused
    /// 可真实触发）；③ 页数用 pg_relation_size/8192（真实字节数，优于 MySQL 的 null）；
    /// ④ 无 last_user_* 时间戳全 null；键列从 pg_get_indexdef 首对括号提取（表达式/INCLUDE 列
    /// 是边界近似，仅展示用）。
    /// </summary>
    public Task<List<Core.Indexes.IndexUsageItem>> GetIndexUsageAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var rows = CreateContext(cfg, dbName).SqlQuery<IndexUsageRaw>(IndexUsageSql);
            return MapIndexUsage(rows);
        }, ct);

    /// <summary>索引使用率查询（常量供单测断言；连接已切目标库，current_database 即 dbName）。</summary>
    internal const string IndexUsageSql = """
        /* dbpilot */
        SELECT current_database()   AS DbName,
               s.schemaname         AS SchemaName,
               s.relname            AS TableName,
               s.indexrelname       AS IndexName,
               i.indisprimary       AS IsPrimary,
               i.indisunique        AS IsUnique,
               s.idx_scan           AS IdxScan,
               COALESCE(tu.n_tup_ins, 0) + COALESCE(tu.n_tup_upd, 0) + COALESCE(tu.n_tup_del, 0) AS TableWrites,
               (pg_relation_size(i.indexrelid) / 8192) AS UsedPages,
               regexp_replace(pg_get_indexdef(i.indexrelid), '^.*?\((.*?)\).*$', '\1') AS KeyColumns
        FROM pg_stat_user_indexes s
        JOIN pg_index i ON i.indexrelid = s.indexrelid
        LEFT JOIN pg_stat_user_tables tu ON tu.relid = s.relid
        ORDER BY s.schemaname, s.relname, s.indexrelname
        """;

    /// <summary>原始行 → 使用率行（纯函数，供单测）：TableName 裸 schema.表（PG 无禁用脚本，展示口径）。</summary>
    internal static List<Core.Indexes.IndexUsageItem> MapIndexUsage(List<IndexUsageRaw> rows)
        => rows.Select(r => new Core.Indexes.IndexUsageItem
        {
            DbName = r.DbName ?? "",
            TableName = $"{r.SchemaName}.{r.TableName}",
            IndexName = r.IndexName,
            IsPrimaryKey = r.IsPrimary,
            TypeDesc = r.IsPrimary ? "CLUSTERED" : "NONCLUSTERED",
            IsUnique = r.IsUnique,
            KeyColumns = r.KeyColumns,
            UserSeeks = r.IdxScan,
            UserScans = 0,
            UserLookups = 0,
            UserUpdates = r.TableWrites,
            UsedPageCount = r.UsedPages,
        }).ToList();

    /// <summary>索引使用率原始行（Chloe 映射）。</summary>
    internal sealed class IndexUsageRaw
    {
        public string? DbName { get; set; }
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string IndexName { get; set; } = "";
        public bool IsPrimary { get; set; }
        public bool IsUnique { get; set; }
        public long IdxScan { get; set; }
        public long TableWrites { get; set; }
        public long UsedPages { get; set; }
        public string? KeyColumns { get; set; }
    }

    // ---- 以下为多引擎能力边界 ----
    // 无对等数据源（功能矩阵定稿，前端隐藏入口/降级形态）：缺失索引 / 碎片 / 死锁 / 计划快照 / 慢SQL事件

    // 永久不支持：PG 无优化器缺失索引建议对等数据源
    public Task<List<Core.Indexes.MissingIndexItem>> GetMissingIndexesAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => throw Unsupported("缺失索引建议");

    // 永久不支持：PG 无页数/碎片率对等数据源（CLUSTER 表可近似，语义不等价不伪造）
    public Task<List<Core.Indexes.FragTableInfo>> GetFragmentTablesAsync(InstanceConfig cfg, string dbName, int minPages, CancellationToken ct = default)
        => throw Unsupported("碎片扫描");

    // 永久不支持：PG 无索引碎片率对等数据源
    public Task<List<Core.Indexes.IndexFragmentItem>> GetIndexFragmentationAsync(InstanceConfig cfg, string dbName, int objectId, int minPages, CancellationToken ct = default)
        => throw Unsupported("碎片扫描");

    // 永久不支持：PG 无历史死锁事件流对等数据源（仅 pg_stat_database.deadlocks 累计计数器，
    // 死锁页走趋势-only 降级形态——dbpilot_instance_metrics 聚合）
    public Task<Core.Deadlocks.DeadlockReadResult> ReadDeadlockEventsAsync(InstanceConfig cfg, Core.Deadlocks.DeadlockCursor? cursor, CancellationToken ct = default)
        => throw Unsupported("死锁事件读取");

    // 永久不支持：PG 无计划缓存/历史聚合对等数据源（EXPLAIN 仅即时估算，auto_explain 走日志文件不通用）
    public Task<List<Core.QueryPlan.QueryPlanRawRow>> GetQueryPlanStatsAsync(InstanceConfig cfg, TopSqlFilter? filter = null, CancellationToken ct = default)
        => throw Unsupported("执行计划快照");

    // 永久不支持：PG 无 ShowPlan XML 对等物
    public Task<Dictionary<string, string>> GetQueryPlanXmlsAsync(InstanceConfig cfg, List<QueryPlanHandleRef> handles, CancellationToken ct = default)
        => throw Unsupported("执行计划 XML 读取");

    // 永久不支持：PG 无 SQL 通道慢日志对等物（log_min_duration_statement 走日志文件，RDS 不通用）。
    // 慢SQL页降级为 Top SQL 累计模板榜（查询侧读 dbpilot_top_sql_delta）；采集整段被
    // SlowSqlCollectService 的 slowSqlEvents=none 能力门跳过，不会调到这里
    public Task EnsureSlowSqlCaptureAsync(InstanceConfig cfg, CancellationToken ct = default)
        => throw Unsupported("慢SQL XE 文件会话");

    public Task<Core.SlowSql.SlowSqlReadResult> PollSlowSqlAsync(InstanceConfig cfg, Core.SlowSql.SlowSqlCursor? cursor, CancellationToken ct = default)
        => throw Unsupported("慢SQL XE 文件会话");

    // ---- 基础设施 ----

    private static PostgreSQLContext CreateContext(InstanceConfig cfg, string? initialCatalog = null)
    {
        // 实例级命令超时（dbpilot_instance.command_timeout_seconds，默认 30=ADO.NET 原生默认）：
        // 大库碎片扫描单表常超 30s（dm_db_index_physical_stats 物理走页），逐实例可调
        var ctx = new PostgreSQLContext(new NpgsqlConnectionFactory(BuildConnectionString(cfg, initialCatalog)));
        if (cfg.CommandTimeoutSeconds > 0)
            ctx.Session.CommandTimeout = cfg.CommandTimeoutSeconds;
        return ctx;
    }

    internal static string BuildConnectionString(InstanceConfig cfg, string? initialCatalog = null)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = cfg.Host,
            Port = cfg.Port,
            Username = cfg.LoginName,
            Password = cfg.Password,
            Timeout = 5,
        };
        builder["Database"] = initialCatalog.IsNullOrEmpty() ? "postgres" : initialCatalog;
        builder["Application Name"] = "DBPilot";   // pg_stat_activity 可见，会话采样自监控识别口径
        return builder.ConnectionString;
    }

    private sealed class NpgsqlConnectionFactory(string connString) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => new UtcKindConnection(connString);
    }

    /// <summary>扩展探测行（Chloe 映射）。</summary>
    private sealed class ExtRow
    {
        public string? Preload { get; set; }
        public string? ExtVersion { get; set; }
    }

    /// <summary>指标聚合原始行（Chloe 映射）。</summary>
    private sealed class MetricsRawRow
    {
        public string? ProductVersion { get; set; }
        public long Xacts { get; set; }
        public long BlksHit { get; set; }
        public long BlksRead { get; set; }
        public long Deadlocks { get; set; }
        public long SeqScan { get; set; }
        public long NumBackends { get; set; }
    }

    private sealed class ProbeRow
    {
        public string? ProductVersion { get; set; }
        public string? VersionNum { get; set; }
        public string? MachineName { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime UtcNow { get; set; }
    }

    private static DbpilotUnsupportedException Unsupported(string feature)
        => DbpilotUnsupportedException.Feature(DbpilotEngines.PostgreSql, feature);
}
