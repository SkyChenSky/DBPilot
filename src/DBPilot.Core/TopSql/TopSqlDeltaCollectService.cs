using Chloe;
using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Collecting;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.PerformanceInsight;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DBPilot.Core.TopSql;

/// <summary>
/// Top SQL 噪音排除配置（appsettings DBPilot:TopSqlExcludePatterns，与实时页同源）。
/// 语义对齐 Job cron：配置缺失 = 默认全集（RDS 自监控/系统巡检特征模式），
/// 显式空数组 = 清空（不排除任何文本）。
/// </summary>
public class TopSqlExcludeOptions
{
    /// <summary>内置默认排除模式（LIKE 语义，方括号字面量须 [[] 转义），挡两类噪音：
    /// ① SQL Server 侧 RDS 自监控/系统巡检脚本（fn_MSxe_read_event_stream、DBCC TRACESTATUS、
    /// 各类系统计数探针）与平台自身方括号写库 %[[dbpilot[_]%；
    /// ② MySQL 侧平台自监控与阿里云 RDS 内部活动：
    /// '%`dbpilot_%' 排除平台库建在 MySQL 上时 Chloe 落库 SQL（反引号表名）——两种 LIKE 语义下 `_`
    /// 均为通配符：MySQL 命中 "`dbpilot_xxx`"（业务裸 SQL 无反引号不受影响），T-SQL 因反引号字面量
    /// 永不命中（SQL Server 平台写库走方括号由 %[[dbpilot[_]% 覆盖），互不干扰；
    /// 其余为平台 Provider 语句特征（SHOW GLOBAL STATUS/SHOW VARIABLES/digest 自查/磁盘聚合/
    /// PROCESSLIST 采样）——RDS 常把 performance_schema_max_sql_text_length 也置 0，QUERY_SAMPLE_
    /// TEXT 全 NULL 导致内联标记排除失效，此为兜底；ha_health_check/START·STOP SLAVE/PURGE MASTER
    /// LOGS/FLUSH SLOW LOGS/event_schema·effective_user 扫描是阿里云 RDS 内部 HA/复制/慢日志/会话
    /// 数监控语句（等价 SQL Server 侧 RDS 巡检脚本排除）；尾部为驱动/会话管理/连接级探测噪音——
    /// MySqlConnector 连接握手（SET NAMES/character_set_results/auto_increment_increment）、
    /// 事务会话（SET AUTOCOMMIT/SET SESSION ISOLATION/START TRANSACTION/SHOW WARNINGS）、
    /// Chloe InsertAsync 追加的 SELECT @@`IDENTITY`（平台库与被监控实例同机时高频出现）、
    /// RDS 代理每连接探测（SELECT @@GLOBAL.*/@@`rds_release_date`/SET SESSION rotate_log_table/
    /// SQL_TSI_SECOND 签名、SHOW SLAVE STATUS 等复制状态巡检）、驱动参数会话（SET @? = ?/
    /// SELECT ?/@@READ_ONLY/@@SESSION）与平台慢SQL水位查询 TIMESTAMPDIFF 签名——
    /// QUERY_SAMPLE_TEXT 无注释时标记排除失效的兜底。
    /// 双引号 "dbpilot_ 是 Chloe.PostgreSQL 标识符签名——(PG,PG) 形态平台库建在被监控实例同机时
    /// 平台落库语句进 pgs 统计的排除口径（Provider SQL 内联了同款双保险）。
    /// T-SQL 文本不含这些词汇/反引号（T-SQL 无 START TRANSACTION/SET NAMES 语句），SQL Server 侧全部天然 no-op。
    /// 注意不能写 [_]（那是 T-SQL 的字面下划线转义；MySQL LIKE 中 [ ] 是普通字面字符，写了永不命中）。</summary>
    public static readonly string[] DefaultPatterns =
    [
        "%fn_MSxe_read_event_stream%",
        "%DBCC TRACESTATUS%",
        "%ConfigurationDefaults%",
        "%#TraceStatus%",
        "%abnormal_db_count%",
        "%[_][$][$]%",
        "%fn_backup_db_config%",
        "%cpu_core_count%",
        "%[[]compatibility_level], [[]collation_name]%",
        "%[[]is_dynamic], [[]is_advanced]%",
        "%[[]default_database_name]%",
        "%AS [[]role_name]%",
        "%sp_sqlagent[_]%",
        "%FROM sys.traces%",
        "%AS [[]logical_name]%",
        "%count(name) from sys.databases%",
        "%AS [[]is_running]%",
        "%@Primary nvarchar(4000)%",
        "%j.[[]name],%j.[[]enabled]%",
        "%AS SizeGB%",
        "%#dummytable%",
        "%[[]dbpilot[_]%",
        "%`dbpilot_%",
        "%\"dbpilot_%",
        "%SHOW GLOBAL STATUS%",
        "%SHOW GLOBAL VARIABLES%",
        "%SHOW VARIABLES%",
        "%SHOW STATUS LIKE%",
        "%SELECT `table_schema` AS `VolumeMountPoint`%",
        "%SELECT `Fingerprint` , `DbName` , `SqlText`%",
        "%`PROCESSLIST_ID` AS `SessionId`%",
        "%ha_health_check%",
        "%START SLAVE%",
        "%STOP SLAVE%",
        "%PURGE MASTER LOGS%",
        "%FLUSH SLOW LOGS%",
        "%`event_schema` AS `db`%",
        "%`effective_user`%",
        "%SHOW SLAVE STATUS%",
        "%SET NAMES%",
        "%SET `AUTOCOMMIT`%",
        "%SET `sql_log_bin`%",
        "%SET `character_set_results`%",
        "%SET SESSION TRANSACTION ISOLATION LEVEL%",
        "%START TRANSACTION%",
        "%SHOW WARNINGS%",
        "%SELECT @@`IDENTITY`%",
        "%auto_increment_increment%",
        "%SELECT @@GLOBAL%",
        "%`rds_release_date`%",
        "%slow_query_log%",
        "%rotate_log_table%",
        "%INTERVAL TIMESTAMPDIFF ( SECOND , NOW ( ) , UTC_TIMESTAMP ( ) ) SECOND%",
        "%TIMESTAMPDIFF ( SQL_TSI_SECOND%",
        "%SET @? =%",
        "%SELECT @@READ_ONLY%",
        "%SELECT @@SESSION%",
        "%SELECT ?%",
    ];

    public List<string> Patterns { get; set; } = [];
}

/// <summary>单指纹累计计数（µs 口径，与 dm_exec_query_stats 一致）。</summary>
public readonly record struct TopSqlCounter(
    long ExecCount, long ElapsedUs, long WorkerUs, long LogicalReads, long PhysicalReads, long Writes);

/// <summary>分钟差值基线（进程内存态；Host 重启或实例重启后重建，只建基线不落库）。</summary>
public class TopSqlBaseline
{
    /// <summary>基线对应实例启动时间（tempdb create_date，UTC）；变化 = 实例重启，DMV 已清零。</summary>
    public DateTime? InstanceStartUtc { get; set; }

    /// <summary>当前基线对应的差值窗口起点（= 上次扫描时刻）。</summary>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>false = 尚未建立基线（首扫/重启重置后），本 tick 只采样不落库。</summary>
    public bool HasBaseline { get; set; }

    public Dictionary<string, TopSqlCounter> Values { get; set; } = [];
}

/// <summary>基线存储（实例 → 基线）。</summary>
public class TopSqlBaselineStore : ISingletonDepend
{
    private readonly ConcurrentDictionary<int, TopSqlBaseline> _map = new();

    public TopSqlBaseline Of(int instanceId) => _map.GetOrAdd(instanceId, _ => new TopSqlBaseline());
}

/// <summary>
/// Top SQL 分钟差值采集（TopSqlDeltaJob 每 60s 调用）：
/// dm_exec_query_stats 是实例启动以来的累计快照，取相邻两次采样按（库, 指纹）求差落 dbpilot_top_sql_delta，
/// 历史页按窗口聚合。规则：
/// ① Host 重启（无基线）/ 实例重启（SqlServerStartTime 变化，DMV 清零）→ 本次只重建基线不落库；
/// ② 新指纹（上一拍没有）→ 落全量差值（计划缓存条目自创建起累计，创建即在本窗口内）；
/// ③ 负差值（计划缓存驱逐后重建，计数器清零）→ 该指纹本拍跳过，基线同步为当前值。
/// 并发读被监控实例、平台库写串行（Chloe 上下文非线程安全）；噪音排除与实时页同源
/// （文本 NULL/RDS 标记/配置特征模式 + 指纹黑名单，见 SqlServerProvider.GetTopSqlRealtimeAsync）。
/// SQL 模板（指纹 → 语句文本）落 dbpilot_sql_template，历史页 JOIN 取文本。
/// </summary>
public class TopSqlDeltaCollectService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    TopSqlBaselineStore baselines, CollectStateStore states, CollectOptions options, TopSqlExcludeOptions excludes) : IDepend
{
    private const int BatchSize = 500;

    /// <summary>采集入口：遍历启用实例并行采样 TopSQL 原始行，串行求差值落库并推进基线。</summary>
    public async Task CollectAllAsync(CancellationToken ct = default)
    {
        var entities = await CollectRunner.LoadEnabledAsync(sp);
        if (entities is null) return;
        var db = sp.GetRequiredService<DbContext>();

        var filter = new TopSqlFilter
        {
            Patterns = excludes.Patterns,
            Fingerprints = await TopSqlService.LoadExcludedFingerprintsAsync(db),
        };

        var collected = new ConcurrentBag<(int InstanceId, List<TopSqlRawRow> Rows)>();

        await CollectRunner.ForEachEnabledAsync(entities, states, options, "TopSQL 差值采集", ct, async (e, token) =>
        {
            var state = states.Of(e.Id, "TopSQL 差值采集");
            var cfg = InstanceConfigResolver.ToConfig(crypto, e);
            var rows = await provider.GetTopSqlRealtimeAsync(cfg, "", filter, token);
            state.OnSuccess();
            if (rows.Count > 0)
                collected.Add((e.Id, rows));
        });

        // 平台库写串行：差值落库 + 基线推进 + SQL 模板补插
        await CollectRunner.PersistAsync("TopSQL 差值落库失败", async () =>
        {
            var startMap = entities.ToDictionary(e => e.Id, e => e.SqlServerStartTime);
            foreach (var (instanceId, rows) in collected)
            {
                DateTime? startUtc = null;
                if (startMap.TryGetValue(instanceId, out var s) && s.HasValue)
                    startUtc = DateTime.SpecifyKind(s.Value, DateTimeKind.Utc);
                await PersistAsync(db, instanceId, startUtc, rows);
            }
        });
    }

    private async Task PersistAsync(DbContext db, int instanceId, DateTime? startUtc, List<TopSqlRawRow> rows)
    {
        var now = DateTime.UtcNow;
        var baseline = baselines.Of(instanceId);

        // 实例重启（DMV 清零，差值全负）：重置基线，本拍只采样
        if (baseline.HasBaseline && startUtc != baseline.InstanceStartUtc)
        {
            baseline.HasBaseline = false;
            Log.Information("实例 {Id} 检测到重启（SqlServerStartTime 变化），TopSQL 基线重置", instanceId);
        }

        List<DbpilotTopSqlDelta> deltas = [];
        if (baseline.HasBaseline)
        {
            (deltas, baseline.Values) = ComputeDeltas(instanceId, baseline.WindowStartUtc, now, baseline.Values, rows);
            if (deltas.Count > 0)
                Log.Information("实例 {Id} TopSQL 差值落库 {Count} 条（窗口 {Start:u} ~ {End:u}）", instanceId, deltas.Count, baseline.WindowStartUtc, now);
        }
        else
        {
            // 首拍/重启重置后：只建基线
            baseline.Values = rows.ToDictionary(r => Key(r.DbName, r.Fingerprint), r => ToCounter(r));
        }

        for (var i = 0; i < deltas.Count; i += BatchSize)
            await db.InsertRangeAsync(deltas.Skip(i).Take(BatchSize).ToList());

        await PersistSqlTemplatesAsync(db, instanceId, rows);

        baseline.InstanceStartUtc = startUtc;
        baseline.WindowStartUtc = now;
        baseline.HasBaseline = true;
    }

    /// <summary>差值计算（纯函数，供单测）：返回差值行与新基线（负差值指纹跳过且基线同步为当前值）。</summary>
    internal static (List<DbpilotTopSqlDelta> Deltas, Dictionary<string, TopSqlCounter> NewValues) ComputeDeltas(
        int instanceId, DateTime windowStart, DateTime windowEnd,
        IReadOnlyDictionary<string, TopSqlCounter> baseline, List<TopSqlRawRow> rows)
    {
        var deltas = new List<DbpilotTopSqlDelta>();
        var newValues = new Dictionary<string, TopSqlCounter>(baseline.Count + 16);

        foreach (var r in rows)
        {
            var key = Key(r.DbName, r.Fingerprint);
            var cur = ToCounter(r);
            newValues[key] = cur;

            if (!baseline.TryGetValue(key, out var prev))
            {
                // 新指纹：计划缓存条目自创建起累计（创建即在本窗口内），落全量差值，整窗归入 windowEnd
                deltas.Add(new DbpilotTopSqlDelta
                {
                    InstanceId = instanceId,
                    Fingerprint = r.Fingerprint,
                    DbName = r.DbName,
                    WindowStart = windowStart,
                    WindowEnd = windowEnd,
                    ExecCount = cur.ExecCount,
                    TotalWorkerMs = SqlConverts.UsToMs(cur.WorkerUs),
                    TotalElapsedMs = SqlConverts.UsToMs(cur.ElapsedUs),
                    TotalLogicalReads = cur.LogicalReads,
                    TotalPhysicalReads = cur.PhysicalReads,
                    TotalWrites = cur.Writes,
                    MaxElapsedMs = SqlConverts.UsToMs(r.MaxElapsedUs),
                    CreateTime = DateTime.UtcNow,
                });
                continue;
            }

            if (cur.ExecCount < prev.ExecCount)
                continue;   // 负差值：计划缓存驱逐重建（计数器清零），本拍跳过，基线已同步为当前值

            var exec = cur.ExecCount - prev.ExecCount;
            if (exec == 0 && cur.ElapsedUs == prev.ElapsedUs && cur.WorkerUs == prev.WorkerUs
                && cur.LogicalReads == prev.LogicalReads && cur.PhysicalReads == prev.PhysicalReads
                && cur.Writes == prev.Writes)
                continue;   // 窗口内零活动

            deltas.Add(new DbpilotTopSqlDelta
            {
                InstanceId = instanceId,
                Fingerprint = r.Fingerprint,
                DbName = r.DbName,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                ExecCount = exec,
                TotalWorkerMs = SqlConverts.UsToMs(cur.WorkerUs - prev.WorkerUs),
                TotalElapsedMs = SqlConverts.UsToMs(cur.ElapsedUs - prev.ElapsedUs),
                TotalLogicalReads = cur.LogicalReads - prev.LogicalReads,
                TotalPhysicalReads = cur.PhysicalReads - prev.PhysicalReads,
                TotalWrites = cur.Writes - prev.Writes,
                MaxElapsedMs = SqlConverts.UsToMs(r.MaxElapsedUs),
                CreateTime = DateTime.UtcNow,
            });
        }

        return (deltas, newValues);
    }

    /// <summary>SQL 模板补插（dbpilot_sql_template，UNIQUE(instance_id, fingerprint)）：只插库中尚无的指纹。</summary>
    private static async Task PersistSqlTemplatesAsync(DbContext db, int instanceId, List<TopSqlRawRow> rows)
    {
        var fresh = rows
            .GroupBy(r => r.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.ExecutionCount).First())
            .ToList();
        if (fresh.Count == 0) return;

        var existing = (await db.Query<DbpilotSqlTemplate>()
                .Where(x => x.InstanceId == instanceId).ToListAsync())
            .Select(x => x.Fingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var r in fresh)
        {
            if (existing.Contains(r.Fingerprint)) continue;
            try
            {
                await db.InsertAsync(new DbpilotSqlTemplate
                {
                    InstanceId = instanceId,
                    Fingerprint = r.Fingerprint,
                    SqlText = (r.SqlText ?? r.FullSqlText ?? "").Trim().Sub(4000),
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "实例 {Id} SQL 模板落库失败（{Fingerprint}）", instanceId, r.Fingerprint);
            }
        }
    }

    internal static string Key(string? dbName, string fingerprint) => $"{dbName ?? ""}|{fingerprint}";

    private static TopSqlCounter ToCounter(TopSqlRawRow r)
        => new(r.ExecutionCount, r.TotalElapsedUs, r.TotalWorkerUs, r.TotalLogicalReads, r.TotalPhysicalReads, r.TotalWrites);
}
