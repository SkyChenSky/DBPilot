using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Chloe.Infrastructure;
using Chloe.MySql;
using DBPilot.Common;
using DBPilot.Core.Blocking;
using DBPilot.Core.Instances;
using DBPilot.Core.InstanceMetrics;
using DBPilot.Core.Providers;
using DBPilot.Core.SlowSql;
using DBPilot.Core.QueryPlan;
using DBPilot.Core.TopSql;
using IDatabaseProvider = DBPilot.Core.Providers.IDatabaseProvider;   // Chloe.Infrastructure 同名接口消歧
using MySqlConnector;

namespace DBPilot.MySql;

/// <summary>
/// MySQL Provider（仅支持 8.0+）。
/// 数据访问与 SQL Server Provider 同构：Chloe SqlQuery 原生 SQL（同步执行，Task.Run 包装），
/// 每实例按配置动态建 MySqlContext（区别于平台库固定连接的 DBPilotMySqlContext）。
/// 指标口径：MySQL 无性能计数器 DMV，以 SHOW GLOBAL STATUS（performance_schema=OFF 也可用，
/// 状态计数器由服务器内核维护）映射成 SQL Server 计数器名 —— 平台侧 ResolveCounters 纯函数零改动；
/// CPU% / PLE / 编译计数等无对等项留 null，前端自然降级。
/// 能力边界：无对等数据源的功能抛 <see cref="DbpilotUnsupportedException"/>；慢SQL走 mysql.slow_log 表通道。
/// 能力矩阵（UnsupportedFeatures）：死锁 XE / 计划快照 / 缺失索引 / 碎片 / 慢SQL XE 文件通道无对等数据源。
/// </summary>
[DbpilotEngine(DbpilotEngines.MySql, UnsupportedFeatures = new string[]
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
    DbpilotCapabilityKeys.DeadlockTrend,   // 计数器本可出趋势，但用户拍板保持 PG 前形态：死锁入口对 MySQL 隐藏
    DbpilotCapabilityKeys.QueryPlanSnapshot,
    DbpilotCapabilityKeys.MissingIndex,
    DbpilotCapabilityKeys.Fragmentation,
    DbpilotCapabilityKeys.SlowSqlTemplates,
    DbpilotCapabilityKeys.OsCpuMem,
    DbpilotCapabilityKeys.Ple,
    DbpilotCapabilityKeys.CompileStats,
    DbpilotCapabilityKeys.BlockedProcesses,
})]
public partial class MySqlProvider : IDatabaseProvider
{
    /// <summary>连接测试：SELECT 1 建连并测延迟，随后逐项自检权限/环境（缺失归入清单，带影响与修复指引）。</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new ConnectionTestResult();
            var watch = Stopwatch.StartNew();

            try
            {
                var ctx = CreateContext(cfg);
                ctx.SqlQuery<int>("/* dbpilot */ SELECT 1 AS Value");
                watch.Stop();
                result.Ok = true;
                result.LatencyMs = (int)watch.ElapsedMilliseconds;

                // 权限/环境自检：逐项探测，失败归入缺失清单（影响 + 修复指引）
                void Add(string permission, string impact, string fix) =>
                    result.MissingPermissions.Add(new MissingPermission { Permission = permission, Impact = impact, FixScript = fix });

                // ① performance_schema=ON（会话采样 / 阻塞 / TopSQL digest / 索引使用率依赖；启动期参数，SET GLOBAL 无效）
                var ps = ctx.SqlQuery<StatusRow>("/* dbpilot */ SHOW VARIABLES LIKE 'performance_schema'")
                    .FirstOrDefault()?.Value;
                if (!string.Equals(ps, "ON", StringComparison.OrdinalIgnoreCase))
                {
                    Add("performance_schema = ON",
                        "会话采样 / 实时阻塞 / Top SQL / 索引使用率不可用（digest 与锁等待数据源依赖 performance_schema）",
                        "RDS 控制台修改参数组 performance_schema=ON 并重启实例（启动期参数，SET GLOBAL 无效）；自建实例 my.cnf [mysqld] 下 performance_schema=ON 后重启");
                }
                else
                {
                    // ② PS 表可读性（SELECT 权限）
                    try { ctx.SqlQuery<int>("/* dbpilot */ SELECT 1 AS Value FROM performance_schema.threads LIMIT 1"); }
                    catch (Exception ex)
                    {
                        Add("SELECT ON performance_schema.*",
                            $"无法读取 performance_schema 表（会话采样 / 阻塞 / Top SQL 不可用）：{ex.GetDeepestException().Message.Sub(200)}",
                            "GRANT SELECT ON performance_schema.* TO '<账号>'@'%';");
                    }

                    // ③ PROCESS 全局权限：threads / INNODB_TRX 按权限过滤其他账号的行（只授 SELECT 时
                    //    只能看到 DBPilot 自身连接）。USER_PRIVILEGES 只回当前账号自己的授权行，无越权泄露
                    if (ctx.SqlQuery<int>(
                            "/* dbpilot */ SELECT COUNT(*) AS Value FROM information_schema.USER_PRIVILEGES WHERE PRIVILEGE_TYPE = 'PROCESS'")
                        .FirstOrDefault() == 0)
                    {
                        Add("PROCESS（全局）",
                            "只能看到 DBPilot 自身连接的会话（threads / INNODB_TRX 按权限过滤其他账号的行），会话采样 / 阻塞不可用",
                            "GRANT PROCESS ON *.* TO '<账号>'@'%';");
                    }
                }

                // ④ mysql.slow_log 可读（慢日志明细数据源）
                try { ctx.SqlQuery<int>("/* dbpilot */ SELECT 1 AS Value FROM mysql.slow_log LIMIT 1"); }
                catch (Exception ex)
                {
                    Add("SELECT ON mysql.slow_log",
                        $"无法读取 mysql.slow_log 表（慢日志明细不可用）：{ex.GetDeepestException().Message.Sub(200)}",
                        "GRANT SELECT ON mysql.slow_log TO '<账号>'@'%';");
                }

                // ⑤ 慢日志开关（运行期可改，缺了 Ensure 采集期也会标注——这里接入时提前告知）
                result.MissingPermissions.AddRange(EvaluateSlowLogVars(QueryVars(ctx,
                    "slow_query_log", "log_output")));

                // ⑥ performance_schema 尺寸参数自检（仅在 PS=ON 时有意义，OFF 已由 ① 覆盖）
                if (string.Equals(ps, "ON", StringComparison.OrdinalIgnoreCase))
                    result.MissingPermissions.AddRange(EvaluatePsSizing(QueryVars(ctx,
                        "performance_schema_max_digest_length", "performance_schema_max_sql_text_length",
                        "performance_schema_events_statements_history_size", "performance_schema_max_statement_stack")));
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

    /// <summary>一次 SHOW VARIABLES 取多参数（名称→值，OrdinalIgnoreCase）。</summary>
    private static Dictionary<string, string> QueryVars(MySqlContext ctx, params string[] names)
        => ctx.SqlQuery<StatusRow>($"/* dbpilot */ SHOW VARIABLES WHERE Variable_name IN ({string.Join(", ", names.Select(n => $"'{n}'"))})")
            .Where(r => !string.IsNullOrEmpty(r?.Variable_name))
            .ToDictionary(r => r.Variable_name!, r => r.Value ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>慢日志开关自检（纯函数，供单测）：slow_query_log=ON + log_output 含 TABLE（运行期可改）。
    /// 采集期 Ensure 也会校验并写 last_error，这里在接入时提前告知。</summary>
    internal static List<MissingPermission> EvaluateSlowLogVars(IReadOnlyDictionary<string, string> vars)
    {
        var list = new List<MissingPermission>();
        if (vars.TryGetValue("slow_query_log", out var slog) && !string.Equals(slog, "ON", StringComparison.OrdinalIgnoreCase))
            list.Add(new MissingPermission
            {
                Permission = $"slow_query_log = ON（当前 {slog}）",
                Impact = "慢 SQL 明细不可用（事件不落 mysql.slow_log 表）",
                FixScript = "SET GLOBAL slow_query_log = ON;（运行期即生效；亦可 RDS 参数组固化）",
            });
        if (vars.TryGetValue("log_output", out var lo) && !lo.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
            list.Add(new MissingPermission
            {
                Permission = $"log_output 含 TABLE（当前 {lo}）",
                Impact = "慢 SQL 明细不可用（log_output 无 TABLE 时 mysql.slow_log 表不收数据）",
                FixScript = "SET GLOBAL log_output = 'TABLE';（运行期即生效；亦可 RDS 参数组固化）",
            });
        return list;
    }

    /// <summary>performance_schema 尺寸参数自检（纯函数，供单测）：偏离官方默认即列指引——RDS 参数组常批量
    /// 置 0 且全部只读（SET GLOBAL 无效须改参数组+重启），症状是"静默空数据"而非报错，接入时一次性告知。
    /// 键缺失（防御：异常环境）不告警。症状口径与 README「MySQL 监控前置参数表」同源。</summary>
    internal static List<MissingPermission> EvaluatePsSizing(IReadOnlyDictionary<string, string> vars)
    {
        const string rdsFix = "RDS 控制台参数组恢复官方默认值并重启实例（只读参数，SET GLOBAL 无效）；自建实例 my.cnf 调整后重启";
        var list = new List<MissingPermission>();

        if (vars.TryGetValue("performance_schema_max_digest_length", out var dl) && ParseLong(dl) <= 0)
            list.Add(new MissingPermission
            {
                Permission = $"performance_schema_max_digest_length = 1024（当前 {dl}）",
                Impact = "Top SQL 榜恒空（digest 全 NULL，静默无报错）",
                FixScript = rdsFix,
            });
        if (vars.TryGetValue("performance_schema_max_sql_text_length", out var stl) && ParseLong(stl) <= 0)
            list.Add(new MissingPermission
            {
                Permission = $"performance_schema_max_sql_text_length = 1024（当前 {stl}）",
                Impact = "SQL 真实样例文本恒空；自监控标记排除降级为特征模式兜底；阻塞\"睡着拿锁头\"的最后语句不可得",
                FixScript = rdsFix,
            });
        if (vars.TryGetValue("performance_schema_events_statements_history_size", out var hs) && ParseLong(hs) <= 0)
            list.Add(new MissingPermission
            {
                Permission = $"performance_schema_events_statements_history_size = 10（当前 {hs}）",
                Impact = "阻塞分析\"睡着拿锁头\"的最后语句补查不可得（events_statements_history 无行）",
                FixScript = rdsFix,
            });
        if (vars.TryGetValue("performance_schema_max_statement_stack", out var ss) && ParseLong(ss) <= 1)
            list.Add(new MissingPermission
            {
                Permission = $"performance_schema_max_statement_stack = 10（当前 {ss}）",
                Impact = "存储过程体内语句连事件都不产生（语句栈深度不足，运行期开 statement/sp 仪器也无效）——语句历史/阻塞语句上下文受损；注：digest 榜在任何配置下都不含过程体内语句（sp/stmt 的 DIGEST 恒 NULL，MySQL 通用边界）",
                FixScript = rdsFix,
            });
        return list;
    }

    private static long ParseLong(string? v) => long.TryParse(v?.Trim(), out var n) ? n : 0;

    /// <summary>版本/环境探测：VERSION()/@@version_comment/@@hostname + Uptime 状态变量推实例启动时间，CpuCores 无对等恒 0。</summary>
    public Task<InstanceMeta> ProbeAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // 版本 / Edition（version_comment 口径与 SQL Server 不同）/ 主机名；CpuCores 无对等（0）
            const string sql = """
                /* dbpilot */
                SELECT VERSION()       AS ProductVersion,
                       @@version_comment AS Edition,
                       @@hostname       AS MachineName,
                       UTC_TIMESTAMP(3) AS UtcNow
                """;

            var ctx = CreateContext(cfg);
            var row = ctx.SqlQuery<ProbeRow>(sql).FirstOrDefault()
                      ?? throw new InvalidOperationException("版本探测无返回行");

            var utcNow = DateTime.SpecifyKind(row.UtcNow, DateTimeKind.Utc);

            // Uptime 状态变量（秒）→ 实例启动时间（等价 SQL Server tempdb create_date 口径，供重启检测）
            var uptime = ctx.SqlQuery<StatusRow>("/* dbpilot */ SHOW GLOBAL STATUS LIKE 'Uptime'")
                .FirstOrDefault()?.Value;
            DateTime? startUtc = long.TryParse(uptime, out var secs) && secs > 0
                ? utcNow.AddSeconds(-secs)
                : null;

            return new InstanceMeta
            {
                ProductVersion = row.ProductVersion ?? "",
                Edition = row.Edition ?? "",
                MachineName = row.MachineName,
                CpuCores = 0,
                SqlServerStartTimeUtc = startUtc,
                ClockSkewSeconds = (int)Math.Abs((utcNow - DateTime.UtcNow).TotalSeconds),
                MajorVersion = ParseMySqlVersion(row.ProductVersion)
            };
        }, ct);

    /// <summary>库列表：排除系统库（information_schema/performance_schema/mysql/sys）与 RDS 回收站，按名称排序。</summary>
    public Task<List<string>> GetDatabasesAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // 排除系统库 + RDS 回收站（__recycle_bin__）
            const string sql = """
                /* dbpilot */
                SELECT schema_name AS Value
                FROM information_schema.schemata
                WHERE schema_name NOT IN ('information_schema', 'performance_schema', 'mysql', 'sys', '__recycle_bin__')
                ORDER BY schema_name
                """;

            return CreateContext(cfg).SqlQuery<string>(sql);
        }, ct);

    /// <summary>实例指标快照：SHOW GLOBAL STATUS 交 BuildMetricsSnapshot 映射成 SQL Server 计数器名，供平台侧统一消费。</summary>
    public Task<InstanceMetricsSnapshot> GetInstanceMetricsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var ctx = CreateContext(cfg);
            var version = ctx.SqlQuery<string>("/* dbpilot */ SELECT VERSION() AS Value").FirstOrDefault();

            // SHOW GLOBAL STATUS 在 performance_schema=OFF 时同样可用（8.0 已移除 information_schema.GLOBAL_STATUS 表）
            var status = ctx.SqlQuery<StatusRow>("/* dbpilot */ SHOW GLOBAL STATUS")
                .Where(r => !r.Variable_name.IsNullOrEmpty())
                .ToDictionary(r => r.Variable_name!, r => r.Value ?? "", StringComparer.OrdinalIgnoreCase);

            return BuildMetricsSnapshot(version, status);
        }, ct);

    /// <summary>
    /// 磁盘用量（库级聚合，无卷级对等数据源）：VolumeMountPoint 列承载库名，AvailableMb 恒 null
    /// （information_schema 无宿主卷可用空间），前端按库一线渲染并注明口径。
    /// </summary>
    public Task<List<InstanceDiskRawRow>> GetInstanceDiskUsageAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            const string sql = """
                /* dbpilot */
                SELECT table_schema AS VolumeMountPoint,
                       CAST(ROUND(SUM(data_length + index_length) / 1048576) AS SIGNED) AS TotalMb,
                       CAST(NULL AS SIGNED) AS AvailableMb
                FROM information_schema.tables
                GROUP BY table_schema
                ORDER BY table_schema
                """;

            return CreateContext(cfg).SqlQuery<InstanceDiskRawRow>(sql);
        }, ct);

    // ---- 会话 / 阻塞（performance_schema + information_schema.INNODB_TRX） ----

    /// <summary>
    /// 活动请求采样（MySQL 口径）：performance_schema.threads（前台线程，排 Sleep/Quit/Daemon/复制类）
    /// + events_statements_current（当前语句 SQL_TEXT + DIGEST 天然指纹 → QueryHash）；阻塞边由 data_lock_waits
    /// 派生（REQUESTING/BLOCKING_THREAD_ID → PROCESSLIST_ID；持锁会话已断的孤儿事务映射 -2 系统节点）。
    /// 与 SQL Server 版语义对齐：只返回活动会话，"睡着拿锁"头走 <see cref="GetHeadBlockersAsync"/> 补查；
    /// 全量返回（通常几十行内），阻塞树组装在平台侧 Core，AAS 采样复用本查询。
    /// 噪音排除：① RDS 内部账号（rdsadmin/aurora/mysql.sys 等——实测 aurora 常驻 17 会话"waiting for brr
    /// transaction"等，不排除会把 AAS 基线恒抬高，等价 SQL Server session_id&lt;50 系统会话排除口径）；
    /// ② 平台自监控会话——连接属性通道（session_connect_attrs）在 RDS 上被参数关死（整表 0 行），
    /// program_name 按连接识别不可用，按语句特征排除：Provider 语句带 `/* dbpilot */` 内联标记、
    /// 平台库写走 Chloe 反引号 `dbpilot_ 表名（(My,My) 同机形态下平台落库会进采样抬高 CPU 桶，
    /// 等价 SQL Server 侧 program_name &lt;&gt; 'DBPilot' 口径）。
    /// 等待分桶：WaitType 用 MySQL 状态词汇（'Waiting for row lock' 等），WaitBuckets 按 "WAITING FOR…LOCK" 归锁桶。
    /// </summary>
    public Task<List<ActiveRequestRow>> GetActiveRequestsAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() => MapActiveRequests(CreateContext(cfg).SqlQuery<ActiveRequestRaw>(ActiveRequestsSql)), ct);

    /// <summary>RDS 内部维护账号清单（会话采样/慢日志噪音排除共用；replicator = Binlog Dump GTID
    /// 复制线程会落 slow_log，aurora 常驻会话不排除会抬高 AAS 基线）。</summary>
    internal const string RdsInternalUsersList = "('rdsadmin', 'aurora', 'mysql.sys', 'mysql.session', 'mysql.infoschema', 'replicator')";

    /// <summary>BIGINT UNSIGNED 会话号收敛 int 的有效域（超界极端值判无效）。</summary>
    private static bool IsValidSessionId(long sessionId) => sessionId is > 0 and <= int.MaxValue;

    /// <summary>主查询（常量供单测断言）：单行每会话（四个 LEFT JOIN 均已去重/分组，无行放大）。</summary>
    internal const string ActiveRequestsSql = """
        /* dbpilot */
        SELECT t.PROCESSLIST_ID   AS SessionId,
               CASE WHEN w.waiting_pid IS NOT NULL OR t.PROCESSLIST_STATE LIKE 'Waiting%'
                    THEN 'suspended' ELSE 'running' END                                    AS Status,
               t.PROCESSLIST_COMMAND                                                        AS Command,
               DATE_SUB(UTC_TIMESTAMP(3), INTERVAL IFNULL(t.PROCESSLIST_TIME, 0) SECOND)    AS StartTimeUtc,
               CASE WHEN w.waiting_pid IS NOT NULL THEN 'Waiting for row lock'
                    WHEN t.PROCESSLIST_STATE LIKE 'Waiting%' THEN t.PROCESSLIST_STATE
                    ELSE NULL END                                                           AS WaitType,
               IFNULL(t.PROCESSLIST_TIME, 0) * 1000                                         AS WaitTimeMs,
               CASE WHEN w.blocking_pid IS NOT NULL THEN w.blocking_pid
                    WHEN w.waiting_pid IS NOT NULL THEN -2
                    ELSE 0 END                                                              AS BlockingSessionId,
               IFNULL(t.PROCESSLIST_TIME, 0) * 1000                                         AS TotalElapsedMs,
               IFNULL(trx.trx_cnt, 0)                                                       AS OpenTranCount,
               t.PROCESSLIST_USER                                                           AS LoginName,
               t.PROCESSLIST_HOST                                                           AS HostName,
               ca.program_name                                                              AS ProgramName,
               t.PROCESSLIST_DB                                                             AS DbName,
               esc.SQL_TEXT                                                                 AS SqlText,
               t.PROCESSLIST_INFO                                                           AS BatchSqlText,
               esc.DIGEST                                                                   AS QueryHash
        FROM performance_schema.threads t
        LEFT JOIN performance_schema.events_statements_current esc ON esc.THREAD_ID = t.THREAD_ID
        LEFT JOIN (
            SELECT trx_mysql_thread_id AS pid, COUNT(*) AS trx_cnt
            FROM information_schema.INNODB_TRX
            GROUP BY trx_mysql_thread_id
        ) trx ON trx.pid = t.PROCESSLIST_ID
        LEFT JOIN (
            SELECT PROCESSLIST_ID AS pid, MAX(CASE WHEN ATTR_NAME = 'program_name' THEN ATTR_VALUE END) AS program_name
            FROM performance_schema.session_connect_attrs
            WHERE PROCESSLIST_ID IS NOT NULL
            GROUP BY PROCESSLIST_ID
        ) ca ON ca.pid = t.PROCESSLIST_ID
        LEFT JOIN (
            SELECT DISTINCT rt.PROCESSLIST_ID AS waiting_pid, bt.PROCESSLIST_ID AS blocking_pid
            FROM performance_schema.data_lock_waits lw
            JOIN performance_schema.threads rt ON rt.THREAD_ID = lw.REQUESTING_THREAD_ID
            LEFT JOIN performance_schema.threads bt ON bt.THREAD_ID = lw.BLOCKING_THREAD_ID
        ) w ON w.waiting_pid = t.PROCESSLIST_ID
        WHERE t.PROCESSLIST_ID IS NOT NULL
          AND t.PROCESSLIST_ID <> CONNECTION_ID()
          AND t.PROCESSLIST_COMMAND NOT IN ('Sleep', 'Quit', 'Daemon', 'Binlog Dump', 'Binlog Dump GTID', 'Register Slave')
          AND NOT (COALESCE(esc.SQL_TEXT, t.PROCESSLIST_INFO, '') LIKE '/* dbpilot */%'
                OR COALESCE(esc.SQL_TEXT, t.PROCESSLIST_INFO, '') LIKE '%`dbpilot_%')
        """ + "\n          AND t.PROCESSLIST_USER NOT IN " + RdsInternalUsersList;

    /// <summary>原始行 → 活动请求行（纯函数，供单测）：BIGINT UNSIGNED 会话号收敛 int，超界极端值跳过。</summary>
    internal static List<ActiveRequestRow> MapActiveRequests(List<ActiveRequestRaw> rows)
        => rows.Where(r => IsValidSessionId(r.SessionId))
            .Select(r => new ActiveRequestRow
            {
                SessionId = (int)r.SessionId,
                Status = r.Status,
                Command = r.Command,
                StartTimeUtc = r.StartTimeUtc,
                WaitType = r.WaitType,
                WaitTimeMs = r.WaitTimeMs,
                BlockingSessionId = (int)(r.BlockingSessionId ?? 0),
                TotalElapsedMs = r.TotalElapsedMs,
                OpenTranCount = r.OpenTranCount,
                LoginName = r.LoginName,
                HostName = r.HostName,
                ProgramName = r.ProgramName,
                DbName = r.DbName,
                SqlText = r.SqlText,
                BatchSqlText = r.BatchSqlText,
                QueryHash = r.QueryHash,
            })
            .ToList();

    /// <summary>
    /// 头阻塞者补查（MySQL 口径）：主查询只回活动会话，"睡着拿锁"（事务开着锁拿着但
    /// Command=Sleep）不在其中。事务信息走 INNODB_TRX（trx_started 为服务器本地时间 → TIMESTAMPDIFF 转 UTC），
    /// 最后执行语句走 events_statements_history 每线程最新事件（threads.PROCESSLIST_INFO 对 Sleep 会话为 NULL）；
    /// MySQL 无 last_request_start/end 对等物，留 null（树组装不消费）。
    /// </summary>
    public Task<List<HeadBlockerRow>> GetHeadBlockersAsync(InstanceConfig cfg, List<int> headSessionIds, CancellationToken ct = default)
        => Task.Run(() => MapHeadBlockers(CreateContext(cfg).SqlQuery<HeadBlockerRaw>(HeadBlockersSql(headSessionIds))), ct);

    /// <summary>补查语句（headSessionIds 由平台侧去重后传入，拼 IN 列表，int 无注入风险）。</summary>
    internal static string HeadBlockersSql(List<int> headSessionIds)
        => $"""
           /* dbpilot */
           SELECT t.PROCESSLIST_ID    AS SessionId,
                  t.PROCESSLIST_USER  AS LoginName,
                  t.PROCESSLIST_HOST  AS HostName,
                  ca.program_name     AS ProgramName,
                  t.PROCESSLIST_DB    AS DbName,
                  IFNULL(trx.trx_cnt, 0) AS OpenTranCount,
                  CASE WHEN trx.trx_started IS NOT NULL THEN
                       DATE_ADD(trx.trx_started, INTERVAL TIMESTAMPDIFF(SECOND, NOW(), UTC_TIMESTAMP()) SECOND)
                       ELSE NULL END AS TransactionBeginUtc,
                  h.sql_text          AS LastSqlText
           FROM performance_schema.threads t
           LEFT JOIN (
               SELECT trx_mysql_thread_id AS pid, COUNT(*) AS trx_cnt, MIN(trx_started) AS trx_started
               FROM information_schema.INNODB_TRX
               GROUP BY trx_mysql_thread_id
           ) trx ON trx.pid = t.PROCESSLIST_ID
           LEFT JOIN (
               SELECT t2.PROCESSLIST_ID AS pid, h2.SQL_TEXT AS sql_text
               FROM performance_schema.events_statements_history h2
               JOIN performance_schema.threads t2 ON t2.THREAD_ID = h2.THREAD_ID
               JOIN (SELECT THREAD_ID AS tid, MAX(EVENT_ID) AS max_event_id
                     FROM performance_schema.events_statements_history
                     GROUP BY THREAD_ID) m ON m.tid = h2.THREAD_ID AND m.max_event_id = h2.EVENT_ID
           ) h ON h.pid = t.PROCESSLIST_ID
           LEFT JOIN (
               SELECT PROCESSLIST_ID AS pid, MAX(CASE WHEN ATTR_NAME = 'program_name' THEN ATTR_VALUE END) AS program_name
               FROM performance_schema.session_connect_attrs
               WHERE PROCESSLIST_ID IS NOT NULL
               GROUP BY PROCESSLIST_ID
           ) ca ON ca.pid = t.PROCESSLIST_ID
           WHERE t.PROCESSLIST_ID IN ({string.Join(", ", headSessionIds)})
           """;

    /// <summary>原始行 → 头阻塞者补查行（纯函数，供单测）：会话号收敛 int，超界极端值跳过。</summary>
    internal static List<HeadBlockerRow> MapHeadBlockers(List<HeadBlockerRaw> rows)
        => rows.Where(r => IsValidSessionId(r.SessionId))
            .Select(r => new HeadBlockerRow
            {
                SessionId = (int)r.SessionId,
                LoginName = r.LoginName,
                HostName = r.HostName,
                ProgramName = r.ProgramName,
                DbName = r.DbName,
                OpenTranCount = r.OpenTranCount,
                TransactionBeginUtc = r.TransactionBeginUtc,
                LastSqlText = r.LastSqlText,
            })
            .ToList();

    /// <summary>
    /// 阻塞原因（锁资源，MySQL 口径）：performance_schema.data_locks —— TABLE/RECORD 两级锁，
    /// 状态映射 WAITING/PENDING → WAIT / GRANTED → GRANT（对齐 BlockTreeBuilder 排序词汇；RDS 8.0.36
    /// 实测枚举是 WAITING/GRANTED，PENDING 一并兼容）；OBJECT_SCHEMA/OBJECT_NAME 直接可得，
    /// 无需 SQL Server 版的三 id 命名空间反查（EntityId 恒 0）。
    /// </summary>
    public Task<List<SessionLockRow>> GetSessionLocksAsync(InstanceConfig cfg, List<int> sessionIds, CancellationToken ct = default)
        => Task.Run(() => MapSessionLocks(CreateContext(cfg).SqlQuery<SessionLockRaw>(SessionLocksSql(sessionIds))), ct);

    /// <summary>锁资源语句（sessionIds 由平台侧树内真实会话传入，拼 IN 列表，int 无注入风险）。</summary>
    internal static string SessionLocksSql(List<int> sessionIds)
        => $"""
           /* dbpilot */
           SELECT t.PROCESSLIST_ID  AS SessionId,
                  l.LOCK_TYPE       AS ResourceType,
                  l.OBJECT_SCHEMA   AS DbName,
                  l.OBJECT_NAME     AS ObjectName,
                  l.LOCK_MODE       AS LockMode,
                  CASE WHEN l.LOCK_STATUS IN ('PENDING', 'WAITING') THEN 'WAIT' ELSE 'GRANT' END AS LockStatus
           FROM performance_schema.data_locks l
           JOIN performance_schema.threads t ON t.THREAD_ID = l.THREAD_ID
           WHERE t.PROCESSLIST_ID IN ({string.Join(", ", sessionIds)})
           """;

    /// <summary>原始行 → 锁资源行（纯函数，供单测）：会话号收敛 int，超界极端值跳过。</summary>
    internal static List<SessionLockRow> MapSessionLocks(List<SessionLockRaw> rows)
        => rows.Where(r => IsValidSessionId(r.SessionId))
            .Select(r => new SessionLockRow
            {
                SessionId = (int)r.SessionId,
                ResourceType = r.ResourceType,
                DbName = r.DbName,
                ObjectName = r.ObjectName,
                LockMode = r.LockMode,
                LockStatus = r.LockStatus,
            })
            .ToList();

    /// <summary>活动请求主查询原始行（Chloe 映射）：PROCESSLIST_ID 是 BIGINT UNSIGNED，先落 long 再收敛 int。</summary>
    internal sealed class ActiveRequestRaw
    {
        public long SessionId { get; set; }
        public string Status { get; set; } = "";
        public string Command { get; set; } = "";
        public DateTime? StartTimeUtc { get; set; }
        public string? WaitType { get; set; }
        public long WaitTimeMs { get; set; }
        public long? BlockingSessionId { get; set; }
        public long TotalElapsedMs { get; set; }
        public int OpenTranCount { get; set; }
        public string? LoginName { get; set; }
        public string? HostName { get; set; }
        public string? ProgramName { get; set; }
        public string? DbName { get; set; }
        public string? SqlText { get; set; }
        public string? BatchSqlText { get; set; }
        public string? QueryHash { get; set; }
    }

    /// <summary>头阻塞者补查原始行（SQL Server 侧无 MySQL 对等物的时间列不查，不出现在结果里）。</summary>
    internal sealed class HeadBlockerRaw
    {
        public long SessionId { get; set; }
        public string? LoginName { get; set; }
        public string? HostName { get; set; }
        public string? ProgramName { get; set; }
        public string? DbName { get; set; }
        public int OpenTranCount { get; set; }
        public DateTime? TransactionBeginUtc { get; set; }
        public string? LastSqlText { get; set; }
    }

    /// <summary>锁资源原始行（data_locks 无数值实体 id，EntityId 恒 0 免反查）。</summary>
    internal sealed class SessionLockRaw
    {
        public long SessionId { get; set; }
        public string ResourceType { get; set; } = "";
        public string? DbName { get; set; }
        public string? ObjectName { get; set; }
        public string LockMode { get; set; } = "";
        public string LockStatus { get; set; } = "";
    }

    /// <summary>SHOW STATUS / SHOW VARIABLES 结果行（Variable_name / Value 两列，Value 为字符串需平台侧解析）。</summary>
    private sealed class StatusRow
    {
        public string? Variable_name { get; set; }
        public string? Value { get; set; }
    }

    private sealed class ProbeRow
    {
        public string? ProductVersion { get; set; }
        public string? Edition { get; set; }
        public string? MachineName { get; set; }
        public DateTime UtcNow { get; set; }
    }

    /// <summary>
    /// SHOW GLOBAL STATUS → 指标快照（纯函数，供单测）：映射成 SQL Server 计数器名交平台侧 ResolveCounters
    /// 统一消费（value/base 配对 / 瞬时与差值分类零改动）；无对等项（CPU / PLE / 编译 / Lazy Write / 阻塞进程数）留 null。
    /// </summary>
    internal static InstanceMetricsSnapshot BuildMetricsSnapshot(string? productVersion, Dictionary<string, string> status)
    {
        long? N(string name) => status.TryGetValue(name, out var v) && long.TryParse(v, out var n) ? n : null;

        var snapshot = new InstanceMetricsSnapshot
        {
            ProductVersion = productVersion,
            IoReads = N("Innodb_data_reads"),
            IoWrites = N("Innodb_data_writes"),
            IoBytesRead = N("Innodb_data_read"),
            IoBytesWritten = N("Innodb_data_written"),
            SqlMemoryKb = N("Innodb_buffer_pool_bytes_data") / 1024,
            Counters = []
        };

        void Add(string counterName, long? value)
        {
            if (value != null)
                snapshot.Counters.Add(new CounterRow { CounterName = counterName, ObjectName = "MySQL Status", CntrValue = value.Value });
        }

        Add("Batch Requests/sec", N("Questions"));
        var commit = N("Com_commit");
        var rollback = N("Com_rollback");
        Add("Transactions/sec", commit.HasValue && rollback.HasValue ? commit + rollback : commit ?? rollback);
        Add("Logins/sec", N("Connections"));
        Add("Full Scans/sec", N("Select_scan"));
        Add("Number of Deadlocks/sec", N("Innodb_deadlocks"));
        Add("Lock Timeouts/sec", N("Innodb_row_lock_timeouts"));
        Add("Lock Waits/sec", N("Innodb_row_lock_waits"));
        Add("User Connections", N("Threads_connected"));

        // 命中率配对（平台侧 value/base×100）：value = 读请求 − 物理读，base = 读请求
        var readReqs = N("Innodb_buffer_pool_read_requests");
        if (readReqs > 0)
        {
            var hit = Math.Max(0, readReqs.Value - (N("Innodb_buffer_pool_reads") ?? 0));
            snapshot.Counters.Add(new CounterRow { CounterName = "Buffer cache hit ratio", ObjectName = "MySQL Status", CntrValue = hit });
            snapshot.Counters.Add(new CounterRow { CounterName = "Buffer cache hit ratio base", ObjectName = "MySQL Status", CntrValue = readReqs.Value });
        }

        return snapshot;
    }

    /// <summary>“8.0.36”→8、 “5.7.44-log”→5；解析失败返回 0。</summary>
    public static int ParseMySqlVersion(string? version)
    {
        if (version.IsNullOrEmpty()) return 0;
        return int.TryParse(version.Split('.')[0], out var major) ? major : 0;
    }

    private static MySqlContext CreateContext(InstanceConfig cfg, string? initialCatalog = null)
        => new(new MySqlConnectionFactory(BuildConnectionString(cfg, initialCatalog)));

    internal static string BuildConnectionString(InstanceConfig cfg, string? initialCatalog = null)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = cfg.Host,
            Port = (uint)cfg.Port,
            UserID = cfg.LoginName,
            Password = cfg.Password,
            ConnectionTimeout = 5,
            ApplicationName = "DBPilot",       // 平台自监控识别口径（慢日志等通道按程序名排除）
            AllowPublicKeyRetrieval = true     // caching_sha2_password 非 TLS 通道需取公钥（内部监控网络）
        };
        if (!initialCatalog.IsNullOrEmpty())
            builder.Database = initialCatalog;
        return builder.ConnectionString;
    }

    private sealed class MySqlConnectionFactory(string connString) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => new MySqlConnection(connString);
    }

    // ---- Top SQL（events_statements_summary_by_digest，DIGEST 天然指纹） ----

    /// <summary>
    /// Top SQL（MySQL 口径）：events_statements_summary_by_digest —— DIGEST 即归一化指纹、
    /// DIGEST_TEXT 归一化模板（SqlText，落 sql_template 的理想语句源）、QUERY_SAMPLE_TEXT 真实样例
    /// （FullSqlText）；累计列与差值语义对齐（平台侧 TopSqlDeltaCollectService.ComputeDeltas 引擎无关）。
    /// 单位：digest 计时器是皮秒，SQL 端 DIV 1000 → µs（平台侧再转 ms）；SUM_CPU_TIME 需 8.0.28+
    /// （events_statements_cpu 消费者关闭时为 0，天然降级）。
    /// 行数口径：SUM_ROWS_EXAMINED 近似逻辑读、SUM_ROWS_AFFECTED 近似写、物理读无对等恒 0、
    /// LAST_SEEN 服务器本地时间 → TIMESTAMPDIFF 转 UTC。
    /// 噪音排除：① DIGEST IS NOT NULL —— performance_schema_max_digest_length=0 时 digest 整体禁用
    /// （实测 RDS 参数组陷阱，全表 DIGEST=NULL），返回空集而非错行；② '%/* dbpilot */%' 排除平台自监控
    /// + '/* rds internal mark */' 前缀排除 RDS 代理官方自留标记（对齐 SQL Server ②；DIGEST_TEXT
    /// 归一化会剥注释，只查 QUERY_SAMPLE_TEXT 即可——max_sql_text_length=0 时样例恒 NULL、本条随之上浮
    /// 失效，靠 ⑤ 模式兜底）；③ 指纹黑名单（页面"排除"按钮，
    /// LOWER(DIGEST) 精确匹配零误伤）；④ 全部库时排 performance_schema/sys/information_schema
    /// （纯插桩内部活动）+ ExcludeSystemDb 兜底再排 mysql；⑤ filter.Patterns 特征模式（与 SQL Server
    /// 同源配置）——T-SQL 特征（含 [[] 转义）在 MySQL LIKE 语义下永不命中为无害 no-op；真正生效的是
    /// 反引号模式 '%`dbpilot_%'：平台库建在 MySQL 上时（(My,My) 形态）Chloe 落库 SQL（反引号表名、
    /// 无内联标记）会污染榜单，靠它排除。另注：RDS 常把 performance_schema_max_
    /// sql_text_length 也置 0 → QUERY_SAMPLE_TEXT 全 NULL，② 的标记排除随之上浮失效（NULL 恒放行），
    /// 此时平台 Provider SQL（SHOW GLOBAL STATUS 等带标记语句）也只剩本条反引号/配置模式可挡，
    /// 部署建议把该参数恢复官方默认 1024。
    /// </summary>
    public Task<List<TopSqlRawRow>> GetTopSqlRealtimeAsync(InstanceConfig cfg, string db, TopSqlFilter? filter = null, CancellationToken ct = default)
        => Task.Run(() => CreateContext(cfg).SqlQuery<TopSqlRawRow>(BuildTopSqlSql(filter ?? new TopSqlFilter()), new { db }), ct);

    /// <summary>Top SQL 全文（internal 供单测）：@db 走参数，黑名单/模式/开关 SQL 内联。</summary>
    internal static string BuildTopSqlSql(TopSqlFilter filter)
    {
        // 指纹黑名单：DIGEST 是 32/64 位 hex，仅接受小写 hex（与页面排除按钮存储格式一致），非法值忽略
        var fps = filter.Fingerprints
            .Where(x => !x.IsNullOrWhiteSpace() && Hex64Regex().IsMatch(x))
            .Take(MaxFingerprints)
            .ToList();
        var fpClause = fps.Count > 0
            ? $"\n              AND LOWER(d.DIGEST) NOT IN ({string.Join(", ", fps.Select(x => $"'{x}'"))})"
            : "";

        // 特征模式（配置，单引号转义 + 限量防注入/防超长 SQL，与 SqlServerProvider 同口径）：作用于 DIGEST_TEXT
        // 归一化模板（标记 ② 查 QUERY_SAMPLE_TEXT，二者分工与 SQL Server 版 st.text 单列不同）
        var patternClause = string.Join("", filter.Patterns
            .Where(x => !x.IsNullOrWhiteSpace())
            .Take(MaxPatterns)
            .Select(x => $"\n              AND d.DIGEST_TEXT NOT LIKE '{x.Trim().Replace("'", "''")}'"));

        return $"""
            /* dbpilot */
            SELECT Fingerprint, DbName, SqlText, FullSqlText, ExecutionCount, TotalElapsedUs, TotalWorkerUs,
                   TotalLogicalReads, TotalPhysicalReads, TotalWrites, MaxElapsedUs, LastExecutionTime
            FROM (
                SELECT LOWER(d.DIGEST)                   AS Fingerprint,
                       d.SCHEMA_NAME                     AS DbName,
                       d.DIGEST_TEXT                     AS SqlText,
                       d.QUERY_SAMPLE_TEXT               AS FullSqlText,
                       d.COUNT_STAR                      AS ExecutionCount,
                       d.SUM_TIMER_WAIT DIV 1000         AS TotalElapsedUs,
                       d.SUM_CPU_TIME DIV 1000           AS TotalWorkerUs,
                       d.SUM_ROWS_EXAMINED               AS TotalLogicalReads,
                       0                                 AS TotalPhysicalReads,
                       d.SUM_ROWS_AFFECTED               AS TotalWrites,
                       d.MAX_TIMER_WAIT DIV 1000         AS MaxElapsedUs,
                       CASE WHEN d.LAST_SEEN IS NULL THEN NULL ELSE
                           DATE_ADD(d.LAST_SEEN, INTERVAL TIMESTAMPDIFF(SECOND, NOW(), UTC_TIMESTAMP()) SECOND)
                       END                               AS LastExecutionTime
                FROM performance_schema.events_statements_summary_by_digest d
                WHERE d.DIGEST IS NOT NULL
                  AND (d.QUERY_SAMPLE_TEXT IS NULL OR (d.QUERY_SAMPLE_TEXT NOT LIKE '%/* dbpilot */%'
                                                      AND d.QUERY_SAMPLE_TEXT NOT LIKE '/* rds internal mark */%')){fpClause}{patternClause}
                  AND (@db = '' OR d.SCHEMA_NAME = @db)
            ) q
            WHERE @db <> ''
               OR q.DbName IS NULL
               OR (q.DbName NOT IN ('performance_schema', 'sys', 'information_schema')
                   AND ({(filter.ExcludeSystemDb ? 1 : 0)} = 0 OR q.DbName <> 'mysql'))
            """;
    }

    /// <summary>小写 hex（≤64 位）校验；<see cref="GeneratedRegexAttribute"/> 编译期生成。</summary>
    [GeneratedRegex("^[0-9a-f]{1,64}$")]
    private static partial Regex Hex64Regex();

    private const int MaxFingerprints = 1000;
    private const int MaxPatterns = 100;

    // ---- 索引使用率（table_io_waits_summary_by_index_usage） ----

    /// <summary>
    /// 索引使用率（MySQL 口径）：table_io_waits_summary_by_index_usage —— 每索引一行（含从未使用
    /// 的索引行，计数 0 —— 等价 SQL Server 版 sys.indexes 出发 LEFT JOIN 的"未使用也可见"修复）；
    /// 键列/唯一性从 information_schema.STATISTICS 取。口径限制（前端容忍，功能矩阵"部分支持"）：
    /// ① COUNT_FETCH 无法区分 seek/scan/lookup → 全记 UserSeeks，Scans/Lookups 恒 0；且实测
    ///    （RDS 8.0.36）写维护本身也计 fetch（INSERT ≈2/行、UPDATE 不碰索引列也 1/行）→ 有写量表
    ///    的二级索引 fetch 永不归零，IsUnused"零读"判定实际不可触发（宁漏报不误报，前端按读占比
    ///    人工判断，功能矩阵"部分支持"）；
    /// ② 写计数去向实测（RDS 8.0.36）：INSERT 只记表级聚合行（INDEX_NAME=NULL）、COUNT_UPDATE 只记
    ///    PRIMARY、二级索引的 UPDATE 维护仅体现为 fetch → 索引行自身拿不到维护计数，
    ///    UserUpdates 统一承载<b>表级写计数</b>（Σ 该表全部行的 ins+upd+del，各操作记不同行无重复），
    ///    前端"更新次数"列为表写量近似；
    /// ③ 无 last_user_* 时间戳、无每索引页数 → 全 null（IsUnused 页数条件在平台侧视为"未知不排除"；
    ///    前端大小 MB 列空）；④ TableName 返回反引号 `` `库`.`表` `` 全限定（对齐 SQL Server 快照行
    ///    [dbo].[表] 的脚本可执行口径）。
    /// </summary>
    public Task<List<Core.Indexes.IndexUsageItem>> GetIndexUsageAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => Task.Run(() => MapIndexUsage(CreateContext(cfg).SqlQuery<IndexUsageRaw>(IndexUsageSql, new { db = dbName })), ct);

    /// <summary>索引使用率查询（常量供单测）：@db 双处消费（插桩表过滤 + STATISTICS 子查询）。</summary>
    internal const string IndexUsageSql = """
        /* dbpilot */
        SELECT t.OBJECT_SCHEMA   AS DbName,
               t.OBJECT_NAME     AS TableName,
               t.INDEX_NAME      AS IndexName,
               s.non_unique      AS NonUnique,
               s.cols            AS KeyColumns,
               t.COUNT_FETCH     AS CountFetch,
               t.COUNT_INSERT    AS CountInsert,
               t.COUNT_UPDATE    AS CountUpdate,
               t.COUNT_DELETE    AS CountDelete
        FROM performance_schema.table_io_waits_summary_by_index_usage t
        LEFT JOIN (
            SELECT TABLE_NAME AS tname, INDEX_NAME AS iname, MAX(NON_UNIQUE) AS non_unique,
                   GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',') AS cols
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db
            GROUP BY TABLE_NAME, INDEX_NAME
        ) s ON s.tname = t.OBJECT_NAME AND s.iname = t.INDEX_NAME
        WHERE t.OBJECT_SCHEMA = @db AND t.INDEX_NAME IS NOT NULL
        ORDER BY t.OBJECT_NAME, t.INDEX_NAME
        """;

    /// <summary>原始行 → 使用率行（纯函数，供单测）：表级聚合行（IndexName NULL）跳过，但先并入表级写计数。</summary>
    internal static List<Core.Indexes.IndexUsageItem> MapIndexUsage(List<IndexUsageRaw> rows)
    {
        // 表级写计数（含 NULL 聚合行）：各操作记不同行（ins→NULL 行 / upd→PRIMARY / del→定位索引），Σ 无重复
        var tableWrites = rows
            .GroupBy(r => (r.DbName ?? "", r.TableName))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.CountInsert + r.CountUpdate + r.CountDelete));

        return rows.Select(r => (Row: r, Index: r.IndexName))
            .Where(x => !x.Index.IsNullOrEmpty())
            .Select(x =>
            {
                var isPk = string.Equals(x.Index, "PRIMARY", StringComparison.OrdinalIgnoreCase);
                return new Core.Indexes.IndexUsageItem
                {
                DbName = x.Row.DbName ?? "",
                TableName = $"`{x.Row.DbName}`.`{x.Row.TableName}`",
                IndexName = x.Index!,
                IsPrimaryKey = isPk,
                TypeDesc = isPk ? "CLUSTERED" : "NONCLUSTERED",
                IsUnique = x.Row.NonUnique == 0,
                KeyColumns = x.Row.KeyColumns,
                UserSeeks = x.Row.CountFetch,
                UserScans = 0,
                UserLookups = 0,
                UserUpdates = tableWrites[(x.Row.DbName ?? "", x.Row.TableName)],
                };
            })
            .ToList();
    }

    /// <summary>索引使用率原始行（Chloe 映射）：NonUnique 可空 = STATISTICS 无该索引（不应出现，防御）。</summary>
    internal sealed class IndexUsageRaw
    {
        public string? DbName { get; set; }
        public string TableName { get; set; } = "";
        public string? IndexName { get; set; }
        public long? NonUnique { get; set; }
        public string? KeyColumns { get; set; }
        public long CountFetch { get; set; }
        public long CountInsert { get; set; }
        public long CountUpdate { get; set; }
        public long CountDelete { get; set; }
    }

    // ---- 以下为多引擎能力边界 ----
    // 无对等数据源（功能矩阵定稿，前端隐藏入口）：缺失索引 / 碎片 / 死锁 / 计划快照

    // 永久不支持：MySQL 无优化器缺失索引建议对等数据源
    public Task<List<Core.Indexes.MissingIndexItem>> GetMissingIndexesAsync(InstanceConfig cfg, string dbName, CancellationToken ct = default)
        => throw Unsupported("缺失索引建议");

    // 永久不支持：information_schema 无表页数/碎片候选对等数据源
    public Task<List<Core.Indexes.FragTableInfo>> GetFragmentTablesAsync(InstanceConfig cfg, string dbName, int minPages, CancellationToken ct = default)
        => throw Unsupported("索引碎片扫描");

    // 永久不支持：MySQL 无索引碎片率对等数据源
    public Task<List<Core.Indexes.IndexFragmentItem>> GetIndexFragmentationAsync(InstanceConfig cfg, string dbName, int objectId, int minPages, CancellationToken ct = default)
        => throw Unsupported("索引碎片扫描");

    // 永久不支持：MySQL 无历史死锁事件流对等数据源（仅 Innodb_deadlocks 计数器）
    public Task<Core.Deadlocks.DeadlockReadResult> ReadDeadlockEventsAsync(InstanceConfig cfg, Core.Deadlocks.DeadlockCursor? cursor, CancellationToken ct = default)
        => throw Unsupported("死锁事件读取");

    // 永久不支持：MySQL 无计划缓存/历史聚合对等数据源（EXPLAIN 仅即时估算）
    public Task<List<Core.QueryPlan.QueryPlanRawRow>> GetQueryPlanStatsAsync(InstanceConfig cfg, TopSqlFilter? filter = null, CancellationToken ct = default)
        => throw Unsupported("执行计划快照");

    // 永久不支持：MySQL 无 ShowPlan XML 对等物
    public Task<Dictionary<string, string>> GetQueryPlanXmlsAsync(InstanceConfig cfg, List<QueryPlanHandleRef> handles, CancellationToken ct = default)
        => throw Unsupported("执行计划 XML 读取");

    // ---- 慢SQL：mysql.slow_log 表通道（替代 SQL Server 的 XE 会话 + fn_xe） ----

    /// <summary>
    /// 慢SQL会话保障（MySQL 口径）：不自建会话，校验实例侧慢日志开关（dynamic 参数，可 SET GLOBAL，
    /// 但 RDS 侧走参数组更稳，报错给修复指引即可）。slow_query_log≠1 或 log_output 不含 TABLE → 抛错
    /// （采集服务捕获后写 last_error，页面显示"明细采集不可用"）。
    /// </summary>
    public Task EnsureSlowSqlCaptureAsync(InstanceConfig cfg, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var row = CreateContext(cfg).SqlQuery<SlowLogSettingRow>(
                    "/* dbpilot */ SELECT @@slow_query_log AS SlowQueryLog, @@log_output AS LogOutput")
                .FirstOrDefault()
                ?? throw new InvalidOperationException("慢日志配置探测无返回行");

            if (row.SlowQueryLog != 1)
                throw new InvalidOperationException(
                    "实例未开启慢日志（slow_query_log=OFF）：RDS 控制台参数组设置 slow_query_log=ON（连同 long_query_time 阈值），自建实例 SET GLOBAL slow_query_log=ON");
            if (!($",{row.LogOutput},").Contains(",TABLE,", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"慢日志输出目标不含 TABLE（当前 log_output={row.LogOutput}）：需含 TABLE（如 SET GLOBAL log_output='TABLE'，RDS 控制台参数组同理），FILE 形态平台不采集");
        }, ct);

    /// <summary>
    /// 慢SQL增量读取（MySQL 口径）：mysql.slow_log 表（CSV 引擎无索引，30s 轮询全表扫可接受）
    /// 按 start_time 服务器本地时钟水位增量 —— 与 XE"重启清零全量重扫"语义一致：重启无水位 = 全量重扫，
    /// ±3ms 指纹判重兜底（at-least-once）。时长 query_time → ms（TIME_TO_SEC*1000 + µs）；时间戳
    /// TIMESTAMPDIFF 转 UTC。噪音排除：① '%/* dbpilot */%' 排平台自监控（标记内联在语句内）；
    /// ② RDS 内部账号（user_host 前缀）③ LIMIT 2000 防单轮打爆。
    /// </summary>
    public Task<SlowSqlReadResult> PollSlowSqlAsync(InstanceConfig cfg, SlowSqlCursor? cursor, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var watermark = cursor?.Watermark;
            var sql = BuildSlowLogSql(watermark is not null);
            var ctx = CreateContext(cfg);
            var rows = watermark is null
                ? ctx.SqlQuery<SlowLogRaw>(sql).ToList()
                : ctx.SqlQuery<SlowLogRaw>(sql, new { wm = watermark }).ToList();

            return new SlowSqlReadResult
            {
                Records = MapSlowLog(rows),
                // 无行保留旧游标（重置 null → 每 tick 全量重扫振荡；XE 通道同款结论）；水位取末行 start_time 原文
                NewCursor = rows.Count > 0 ? new SlowSqlCursor { Watermark = rows[^1].Watermark } : cursor,
            };
        }, ct);

    /// <summary>watermark 非空时严格 ＞ 比较（µs 精度同刻多事件概率极低，LIMIT 截断丢同行由判重兜底）。
    /// 噪音排除：① 内联标记 ② Chloe 平台写库（反引号 `dbpilot_ 表引用是 Chloe CRUD 签名——(My,My)
    /// 形态平台库与被监控实例同机，平台自身 INSERT 偶发超 1s 会进 slow_log，与 TopSQL DefaultPatterns
    /// 同口径；用户手写 SQL 通常不带反引号，冒烟库 dbpilot_smoke 同前缀属已知取舍）③ RDS 内部账号
    /// （含 replicator——Binlog Dump GTID 复制线程会落 slow_log）。</summary>
    internal static string BuildSlowLogSql(bool hasWatermark)
    {
        var wm = hasWatermark ? "  AND start_time > @wm\n" : "";
        return $"""
            /* dbpilot */
            SELECT DATE_ADD(start_time, INTERVAL TIMESTAMPDIFF(SECOND, NOW(), UTC_TIMESTAMP()) SECOND) AS EventTimeUtc,
                   DATE_FORMAT(start_time, '%Y-%m-%d %H:%i:%s.%f')                                       AS Watermark,
                   db                                                                                     AS DbName,
                   CONVERT(sql_text USING utf8mb4)                                                        AS SqlText,
                   user_host                                                                              AS UserHost,
                   thread_id                                                                              AS ThreadId,
                   TIME_TO_SEC(query_time) * 1000 + MICROSECOND(query_time) DIV 1000                      AS DurationMs,
                   rows_examined                                                                          AS RowsExamined,
                   rows_sent                                                                              AS RowsSent
            FROM mysql.slow_log
            WHERE CONVERT(sql_text USING utf8mb4) NOT LIKE '%/* dbpilot */%'
              AND CONVERT(sql_text USING utf8mb4) NOT LIKE '%`dbpilot_%'
              AND SUBSTRING_INDEX(user_host, '[', 1) NOT IN {RdsInternalUsersList}
            {wm}ORDER BY start_time
            LIMIT 2000
            """;
    }

    /// <summary>慢日志原始行（Chloe 映射；sql_text 是 mediumblob，SELECT 侧 CONVERT utf8mb4）。</summary>
    internal sealed class SlowLogRaw
    {
        public DateTime EventTimeUtc { get; set; }
        public string Watermark { get; set; } = "";
        public string? DbName { get; set; }
        public string SqlText { get; set; } = "";
        public string UserHost { get; set; } = "";
        public long ThreadId { get; set; }
        public long DurationMs { get; set; }
        public long? RowsExamined { get; set; }
        public long? RowsSent { get; set; }
    }

    internal static List<SlowSqlRecord> MapSlowLog(List<SlowLogRaw> rows)
    {
        List<SlowSqlRecord> result = [];
        foreach (var r in rows)
        {
            var text = r.SqlText.Trim();
            if (text.Length == 0) continue;   // 空文本无指纹价值，跳过（与 XE 通道口径一致）

            var (login, host) = ParseUserHost(r.UserHost);
            result.Add(new SlowSqlRecord
            {
                EventTimeUtc = DateTime.SpecifyKind(r.EventTimeUtc, DateTimeKind.Utc),
                DbName = string.IsNullOrWhiteSpace(r.DbName) ? null : r.DbName.Trim(),
                LoginName = login,
                HostName = host,
                AppName = null,              // slow_log 无 client 程序名列，自然降级
                SessionId = r.ThreadId > 0 && r.ThreadId <= int.MaxValue ? (int)r.ThreadId : null,
                SqlType = 2,
                DurationMs = r.DurationMs,
                LogicalReads = r.RowsExamined,
                RowCount = r.RowsSent,
                Fingerprint = SlowSqlEventParser.FingerprintOf(text),
                SqlText = text.Length > SlowSqlEventParser.MaxTextLength ? text[..SlowSqlEventParser.MaxTextLength] : text,
            });
        }
        return result;
    }

    /// <summary>user_host 形如 "user[host] @ proxy [ip]"：[ 前为账号、首对 [] 内为主机；缺形态返 null。</summary>
    internal static (string? Login, string? Host) ParseUserHost(string? userHost)
    {
        if (string.IsNullOrWhiteSpace(userHost)) return (null, null);
        var s = userHost.Trim();
        var open = s.IndexOf('[');
        if (open < 0) return (NullIfEmpty(s), null);
        var close = s.IndexOf(']', open + 1);
        if (close < 0) return (NullIfEmpty(s[..open]), null);
        return (NullIfEmpty(s[..open]), NullIfEmpty(s[(open + 1)..close]));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>慢日志开关探测行（Chloe 映射）。</summary>
    private sealed class SlowLogSettingRow
    {
        public int SlowQueryLog { get; set; }
        public string LogOutput { get; set; } = "";
    }

    private static DbpilotUnsupportedException Unsupported(string feature)
        => DbpilotUnsupportedException.Feature(DbpilotEngines.MySql, feature);
}
