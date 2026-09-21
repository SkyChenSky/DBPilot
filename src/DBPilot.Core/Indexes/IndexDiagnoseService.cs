using Chloe;
using System.Collections.Concurrent;
using DBPilot.Common;
using DBPilot.Core.Crypto;
using DBPilot.Core.Instances;
using DBPilot.Core.Providers;
using DBPilot.Storage;
using DBPilot.Storage.Dialect;
using DBPilot.Storage.Entities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DBPilot.Core.Indexes;

/// <summary>
/// 索引诊断服务：
/// 缺失索引 = 快照单一口径：IndexSnapshotJob 每日 03:10 追加快照（原始建议行，支持"近 7 天新增"差集），
/// 页面「重新采集」按钮经 RecollectAsync 对单实例立即补采（与 Job 同一代码路径）；
/// 索引使用率 = 同一套快照口径：每日 Job 追加（IsUnused 采集时固化、碎片率 LIMITED 逐表扫描并入），
/// 页面经 RecollectUsageAsync 立即补采（独立冷却字典，与缺失索引互不干扰）。
/// </summary>
public class IndexDiagnoseService(IServiceProvider sp, AesGcmCrypto crypto, IDatabaseProvider provider,
    ProviderRegistry registry, IPlatformDialect dialect) : IDepend
{
    /// <summary>实例运行不足 30 天 → DMV 计数随重启清零、快照口径不完整，结果附警示（与 Top SQL 各自声明，语义同源）。</summary>
    private const int DataIncompleteDays = 30;
    private const int SnapshotNewDays = 7;
    private const int SnapshotBatchSize = 500;

    /// <summary>碎片采集页数下限：0 = 全部表都扫（对齐阿里云口径；<1000 页仍有"小表不建议"标注兜底）。</summary>
    private const int FragCollectMinPages = 0;

    /// <summary>碎片逐表扫描间隔（ms）：保护目标实例 IO（Job/手动共用路径一律保留）。</summary>
    private const int FragTableIntervalMs = 200;

    /// <summary>手动重新采集冷却：距上次采集（Job 或手动，任意路径）不足此间隔直接拒绝。</summary>
    private static readonly TimeSpan RecollectCooldown = TimeSpan.FromMinutes(5);

    /// <summary>实例 → 最近一次缺失索引快照采集时刻（静态：服务是 scoped，冷却窗口须跨请求）。</summary>
    private static readonly ConcurrentDictionary<int, DateTime> LastCollectUtc = new();

    /// <summary>实例 → 最近一次索引使用率快照采集时刻（与缺失索引分开，两页冷却互不干扰）。</summary>
    private static readonly ConcurrentDictionary<int, DateTime> LastUsageCollectUtc = new();

    private static readonly HashSet<string> SystemDbs = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb" };

    /// <summary>空批次标记行 table_name：本次采集 DMV 0 条建议时落一行推进批次时间（建好索引后建议归零场景）。</summary>
    internal const string EmptyBatchMarker = "__empty_batch__";

    private static bool IsEmptyBatch(DbpilotMissingIndexSnapshot x) => x.TableName == EmptyBatchMarker;

    private static bool IsUsageEmptyBatch(DbpilotIndexUsageSnapshot x) => x.TableName == EmptyBatchMarker;

    /// <summary>手动重新采集冷却检查（缺失索引/使用率共用，冷却字典各别）：窗口内返回拒绝文案，否则 null。</summary>
    private static string? CooldownReject(ConcurrentDictionary<int, DateTime> lastCollect, int instanceId)
    {
        if (!lastCollect.TryGetValue(instanceId, out var last)
            || DateTime.UtcNow - last >= RecollectCooldown)
            return null;

        var wait = Math.Ceiling((RecollectCooldown - (DateTime.UtcNow - last)).TotalMinutes);
        return $"采集过于频繁，请约 {wait} 分钟后再试";
    }

    /// <summary>
    /// 缺失索引每日快照采集（IndexSnapshotJob 调用）：全部启用实例逐库实时查 DMV
    /// → 原始建议行追加落库；同实例整批统一 snapshot_time。
    /// </summary>
    public Task CollectMissingSnapshotsAsync(CancellationToken ct = default)
        => CollectAllInstancesAsync("缺失索引快照", CollectInstanceAsync, ct);

    /// <summary>手动重新采集（页面按钮）：对该实例立即跑一次快照采集（与每日 Job 同一代码路径）；
    /// 冷却窗口内拒绝，防止频繁采集。</summary>
    public Task<ServiceResult<bool>> RecollectAsync(int instanceId)
        => RecollectInternalAsync(instanceId, LastCollectUtc, CollectInstanceAsync);

    /// <summary>全部启用实例快照采集（缺失索引/使用率两 Job 入口共用）：逐实例调用采集方法，
    /// Unsupported 静默跳过、其余异常记日志不中断其他实例。</summary>
    private async Task CollectAllInstancesAsync(string label, Func<DbpilotInstance, Task> collectInstance, CancellationToken ct)
    {
        var db = sp.GetService<DbContext>();
        if (db is null) return;

        var entities = await db.Query<DbpilotInstance>().Where(x => x.Enabled).ToListAsync();
        foreach (var e in entities)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await collectInstance(e);
            }
            catch (DbpilotUnsupportedException ex)
            {
                Log.Debug("实例 {Id}（{Name}）{Label}跳过：{Reason}", e.Id, e.Name, label, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "实例 {Id}（{Name}）{Label}采集失败", e.Id, e.Name, label);
            }
        }
    }

    /// <summary>手动重新采集共用骨架（缺失索引/使用率）：冷却窗口内拒绝；
    /// Unsupported 文案直出（不做"内部错误"归并）、其余 FriendlyMessage。</summary>
    private async Task<ServiceResult<bool>> RecollectInternalAsync(
        int instanceId, ConcurrentDictionary<int, DateTime> lastCollect, Func<DbpilotInstance, Task> collectInstance)
    {
        var (err, entity) = await LoadInstanceAsync(instanceId);
        if (err != null) return ServiceResult<bool>.Failed(err);

        var reject = CooldownReject(lastCollect, instanceId);
        if (reject != null) return ServiceResult<bool>.Failed(reject);

        try
        {
            await collectInstance(entity!);
        }
        catch (DbpilotUnsupportedException ex)
        {
            return ServiceResult<bool>.Failed(ex.Message);   // 引擎能力边界文案直出（不做"内部错误"归并）
        }
        catch (DbpilotInaccessibleDbsException ex)
        {
            return ServiceResult<bool>.Failed(ex.Message);   // 账号缺库级访问文案直出（含修复指引）
        }
        catch (Exception ex)
        {
            return ServiceResult<bool>.Failed($"采集失败：{ex.FriendlyMessage()}");
        }
        return ServiceResult<bool>.Succeeded(true);
    }

    /// <summary>单实例采集：逐库查 DMV → 原始建议行（不合并/不标注，组合键跨日稳定）追加落库；失败抛出。</summary>
    private async Task CollectInstanceAsync(DbpilotInstance e)
    {
        // 引擎能力守卫（能力矩阵由引擎包自声明）——不拦的话逐库裸 catch 会吞掉 Unsupported 异常，
        // 不支持的引擎每晚报空批次标记行
        if (!registry.Supports(e.Engine, DbpilotFeatures.MissingIndex))
            throw DbpilotUnsupportedException.Feature(e.Engine, DbpilotFeatures.MissingIndex);

        var db = sp.GetService<DbContext>() ?? throw new InvalidOperationException(InstanceConfigResolver.DbNotConfigured);
        var cfg = ToConfig(e);
        var onlineSupported = e.Edition?.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) == true;
        var snapTime = DateTime.UtcNow;

        var rows = new List<DbpilotMissingIndexSnapshot>();
        var filteredStale = 0;
        var targets = await TargetDatabasesAsync(cfg, null);
        var skipped = new List<string>();
        foreach (var dbName in targets)
        {
            try
            {
                // 现有索引键列（供残留过滤：DMV 条目不保证随索引创建清除，残留到内存压力/重启）
                var existing = (await provider.GetIndexUsageAsync(cfg, dbName))
                    .Where(x => !x.KeyColumns.IsNullOrEmpty())
                    .GroupBy(x => x.TableName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var i in await provider.GetMissingIndexesAsync(cfg, dbName))
                {
                    // 建议键列已被现有索引前缀覆盖（用户已建索引）→ 不落库，从源头挡住干扰数据
                    if (existing.TryGetValue(i.TableName, out var indexes)
                        && indexes.Any(ix => IndexScriptBuilder.IsCoveredByExisting(
                               i.EqualityColumns, i.InequalityColumns, ix.KeyColumns)))
                    {
                        filteredStale++;
                        continue;
                    }

                    rows.Add(new DbpilotMissingIndexSnapshot
                    {
                        InstanceId = e.Id,
                        DbName = dbName,
                        TableName = i.TableName.Sub(256),
                        EqualityColumns = i.EqualityColumns,
                        InequalityColumns = i.InequalityColumns,
                        IncludedColumns = i.IncludedColumns,
                        UserSeeks = i.UserSeeks,
                        AvgTotalUserCost = i.AvgTotalUserCost,
                        AvgUserImpact = i.AvgUserImpact,
                        Score = i.Score,
                        LastUserSeek = i.LastUserSeek,
                        TablePages = (int?)i.TablePages,
                        TableRows = i.TableRows,
                        CreateIndexSql = IndexScriptBuilder.BuildCreateIndexSql(
                            i.TableName, i.EqualityColumns, i.InequalityColumns, i.IncludedColumns, onlineSupported),
                        SnapshotTime = snapTime,
                    });
                }
            }
            catch (Exception ex)
            {
                // 不可访问的库跳过，不影响其他库（Warning 留痕：权限缺失时页面只剩空批次，
                // 无日志则表现为"静默没数据"——实测 RDS 账号无库内用户映射即落入此分支）
                Log.Warning(ex, "实例 {Id} 缺失索引采集跳过库 {Db}", e.Id, dbName);
                skipped.Add(dbName);
            }
        }

        // 全部目标库被跳过 = 假成功陷阱（落空批次标记行会掩盖"一个库都没采到"）：
        // 不落库直接抛错——Job 路径记 Error，手动采集路径文案直达页面
        if (targets.Count > 0 && skipped.Count == targets.Count)
            throw DbpilotInaccessibleDbsException.Create("缺失索引", skipped);

        // 空批次标记行（本批 DMV 0 条建议时推进批次时间）+ 分批落库 + 冷却刷新
        await PersistSnapshotsAsync(db, e.Id, rows, snapTime, LastCollectUtc,
            () => new DbpilotMissingIndexSnapshot
            {
                InstanceId = e.Id,
                DbName = string.Empty,
                TableName = EmptyBatchMarker,
                UserSeeks = 0,
                AvgTotalUserCost = 0,
                AvgUserImpact = 0,
                Score = 0,
                CreateIndexSql = EmptyBatchMarker,
                SnapshotTime = snapTime,
            },
            "实例 {Id} 缺失索引快照落库 {Count} 条（过滤已建索引残留 {Filtered} 条，{Time:u}）",
            e.Id, rows.Count, filteredStale, snapTime);
    }

    /// <summary>最新缺失索引快照 + 近 7 天新增标记。db 为空 = 全部库；无快照返回空列表。
    /// 非 SQL Server 实例返回明确报错（引擎守卫——缺失索引 DMV 为 SQL Server 专有）。</summary>
    public async Task<ServiceResult<MissingIndexSnapshotResult>> GetMissingSnapshotAsync(int instanceId, string? db, bool excludeSystemDb = false)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<MissingIndexSnapshotResult>.Failed(InstanceConfigResolver.DbNotConfigured);

        var reject = await InstanceConfigResolver.RejectUnsupportedAsync(dbc, registry, instanceId, DbpilotFeatures.MissingIndex);
        if (reject != null) return ServiceResult<MissingIndexSnapshotResult>.Failed(reject);

        List<DbpilotMissingIndexSnapshot> rows;
        try
        {
            rows = await dbc.Query<DbpilotMissingIndexSnapshot>()
                .Where(x => x.InstanceId == instanceId)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return ServiceResult<MissingIndexSnapshotResult>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }

        // db 过滤保留空批次标记行（否则按库过滤后"最新批次"退回旧数据）
        if (!db.IsNullOrEmpty())
            rows = rows.Where(x => x.DbName.Equals(db, StringComparison.OrdinalIgnoreCase) || IsEmptyBatch(x)).ToList();

        // "全部库"视图排除系统库（页面开关；空批次标记行 db 为空串不受影响）
        if (excludeSystemDb && db.IsNullOrEmpty())
            rows = rows.Where(x => !SystemDbs.Contains(x.DbName)).ToList();

        var result = BuildSnapshotResult(rows);
        return ServiceResult<MissingIndexSnapshotResult>.Succeeded(result);
    }

    /// <summary>快照行 → 展示结果（纯函数，供单测）：最新一批行 + IsNew = 组合不在 7 天前历史快照并集。</summary>
    internal static MissingIndexSnapshotResult BuildSnapshotResult(
        List<DbpilotMissingIndexSnapshot> rows, int newDays = SnapshotNewDays)
    {
        if (rows.Count == 0)
            return new MissingIndexSnapshotResult();

        var latest = rows.Max(x => x.SnapshotTime);
        var cutoff = latest.AddDays(-newDays);
        var oldKeys = new HashSet<string>(
            rows.Where(x => x.SnapshotTime < cutoff && !IsEmptyBatch(x))
                .Select(x => MissingIndexKey(x.DbName, x.TableName, x.EqualityColumns, x.InequalityColumns, x.IncludedColumns)),
            StringComparer.OrdinalIgnoreCase);

        var items = rows
            .Where(x => x.SnapshotTime == latest && !IsEmptyBatch(x))
            .Select(x => new MissingIndexSnapshotItem
            {
                DbName = x.DbName,
                TableName = x.TableName,
                EqualityColumns = x.EqualityColumns,
                InequalityColumns = x.InequalityColumns,
                IncludedColumns = x.IncludedColumns,
                UserSeeks = x.UserSeeks,
                AvgTotalUserCost = x.AvgTotalUserCost,
                AvgUserImpact = x.AvgUserImpact,
                Score = x.Score,
                LastUserSeek = x.LastUserSeek is null ? null : DateTime.SpecifyKind(x.LastUserSeek.Value, DateTimeKind.Utc),
                TablePages = x.TablePages,
                TableRows = x.TableRows,
                CreateIndexSql = x.CreateIndexSql,
                IsNew = !oldKeys.Contains(MissingIndexKey(x.DbName, x.TableName, x.EqualityColumns, x.InequalityColumns, x.IncludedColumns)),
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        return new MissingIndexSnapshotResult
        {
            Items = items,
            Overview = BuildOverview(items.Select(i => new MissingIndexItem
            {
                AvgUserImpact = i.AvgUserImpact,
                LastUserSeek = i.LastUserSeek,
            }).ToList()),
            SnapshotTimeUtc = DateTime.SpecifyKind(latest, DateTimeKind.Utc),
        };
    }

    /// <summary>缺失索引变化趋势（按快照批次计数，时间升序）。db 为空 = 全部库。
    /// 非 SQL Server 实例返回明确报错（引擎守卫）。</summary>
    public async Task<ServiceResult<List<MissingIndexTrendPoint>>> GetMissingTrendAsync(int instanceId, string? db, bool excludeSystemDb = false)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<List<MissingIndexTrendPoint>>.Failed(InstanceConfigResolver.DbNotConfigured);

        var reject = await InstanceConfigResolver.RejectUnsupportedAsync(dbc, registry, instanceId, DbpilotFeatures.MissingIndex);
        if (reject != null) return ServiceResult<List<MissingIndexTrendPoint>>.Failed(reject);

        List<DbpilotMissingIndexSnapshot> rows;
        try
        {
            rows = await dbc.Query<DbpilotMissingIndexSnapshot>()
                .Where(x => x.InstanceId == instanceId)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return ServiceResult<List<MissingIndexTrendPoint>>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }

        // db 过滤保留空批次标记行（空批次要显示 0 值趋势点）
        if (!db.IsNullOrEmpty())
            rows = rows.Where(x => x.DbName.Equals(db, StringComparison.OrdinalIgnoreCase) || IsEmptyBatch(x)).ToList();

        // "全部库"视图排除系统库（页面开关）
        if (excludeSystemDb && db.IsNullOrEmpty())
            rows = rows.Where(x => !SystemDbs.Contains(x.DbName)).ToList();

        return ServiceResult<List<MissingIndexTrendPoint>>.Succeeded(BuildTrend(rows));
    }

    /// <summary>快照行 → 趋势点（纯函数，供单测）：按 snapshot_time 分组计数（空批次标记行不计入条数）。</summary>
    internal static List<MissingIndexTrendPoint> BuildTrend(List<DbpilotMissingIndexSnapshot> rows)
        => rows.GroupBy(x => x.SnapshotTime)
            .Select(g => new MissingIndexTrendPoint
            {
                SnapshotTimeUtc = DateTime.SpecifyKind(g.Key, DateTimeKind.Utc),
                Count = g.Count(x => !IsEmptyBatch(x)),
            })
            .OrderBy(p => p.SnapshotTimeUtc)
            .ToList();

    /// <summary>组合键（库+表+三组列）：跨日差集比对口径，列顺序/大小写不敏感。</summary>
    internal static string MissingIndexKey(
        string dbName, string tableName, string? equality, string? inequality, string? included)
        => string.Join("|",
            dbName.Trim(), tableName.Trim(),
            (equality ?? "").Trim(), (inequality ?? "").Trim(), (included ?? "").Trim());

    // ---------------- 索引使用率快照（使用率 + 未使用判定 + 碎片率合并口径） ----------------

    /// <summary>
    /// 索引使用率每日快照采集（IndexSnapshotJob 在缺失索引之后串行调用）：全部启用实例逐库
    /// 查 DMV 使用率（IsUnused 固化落库）+ LIMITED 碎片逐表扫描并入同一快照行；同实例整批统一 snapshot_time。
    /// </summary>
    public Task CollectUsageSnapshotsAsync(CancellationToken ct = default)
        => CollectAllInstancesAsync("索引使用率快照", CollectUsageInstanceAsync, ct);

    /// <summary>手动重新采集（页面按钮）：对该实例立即跑一次使用率快照采集（与每日 Job 同一代码路径）；
    /// 冷却窗口内拒绝，防止频繁采集（独立字典，与缺失索引冷却互不干扰）。</summary>
    public Task<ServiceResult<bool>> RecollectUsageAsync(int instanceId)
        => RecollectInternalAsync(instanceId, LastUsageCollectUtc, CollectUsageInstanceAsync);

    /// <summary>
    /// 单实例使用率采集：逐库 DMV 使用率行（IsUnused 固化）+ 同库碎片扫描（TableName|IndexName 归一取
    /// MAX 最差分区；每表 200ms 间隔保护目标实例 IO）→ 追加落库；失败抛出。
    /// </summary>
    private async Task CollectUsageInstanceAsync(DbpilotInstance e)
    {
        var db = sp.GetService<DbContext>() ?? throw new InvalidOperationException(InstanceConfigResolver.DbNotConfigured);
        var cfg = ToConfig(e);
        var snapTime = DateTime.UtcNow;
        // 碎片扫描能力矩阵判定（dm_db_index_physical_stats 无对等数据源的引擎跳过，
        // 而非让 GetFragmentTablesAsync 抛 Unsupported 被下方逐库 catch 吞掉全部行）
        var withFrag = registry.Supports(e.Engine, DbpilotFeatures.Fragmentation);

        var rows = new List<DbpilotIndexUsageSnapshot>();
        var targets = await TargetDatabasesAsync(cfg, null);
        var skipped = new List<string>();
        foreach (var dbName in targets)
        {
            try
            {
                var dbRows = new List<DbpilotIndexUsageSnapshot>();
                foreach (var i in await provider.GetIndexUsageAsync(cfg, dbName))
                {
                    dbRows.Add(new DbpilotIndexUsageSnapshot
                    {
                        InstanceId = e.Id,
                        DbName = dbName,
                        TableName = i.TableName.Sub(256),
                        IndexName = i.IndexName.Sub(256),
                        IsPrimaryKey = i.IsPrimaryKey,
                        IsUnique = i.IsUnique,
                        TypeDesc = i.TypeDesc,
                        UserSeeks = i.UserSeeks,
                        UserScans = i.UserScans,
                        UserLookups = i.UserLookups,
                        UserUpdates = i.UserUpdates,
                        LastUserSeek = i.LastUserSeek,
                        LastUserScan = i.LastUserScan,
                        KeyColumns = i.KeyColumns,
                        UsedPageCount = i.UsedPageCount,
                        LastUserUpdate = i.LastUserUpdate,
                        IsUnused = IndexScriptBuilder.IsUnused(i),
                        SnapshotTime = snapTime,
                    });
                }

                // 碎片并入每日快照：LIMITED 模式逐表扫描（大表优先），多分区归一取 MAX
                if (withFrag)
                {
                    var fragMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in await provider.GetFragmentTablesAsync(cfg, dbName, FragCollectMinPages))
                    {
                        foreach (var f in await provider.GetIndexFragmentationAsync(cfg, dbName, t.ObjectId, FragCollectMinPages))
                        {
                            var key = $"{f.TableName}|{f.IndexName}";
                            if (!fragMap.TryGetValue(key, out var cur) || f.AvgFragmentationPercent > cur)
                                fragMap[key] = f.AvgFragmentationPercent;
                        }
                        await Task.Delay(FragTableIntervalMs);
                    }

                    foreach (var r in dbRows)
                        if (fragMap.TryGetValue($"{r.TableName}|{r.IndexName}", out var frag))
                            r.AvgFragmentationPercent = frag;
                }

                rows.AddRange(dbRows);
            }
            catch (Exception ex)
            {
                // 不可访问的库跳过，不影响其他库（Warning 留痕排查静默空批次）
                Log.Warning(ex, "实例 {Id} 使用率采集跳过库 {Db}", e.Id, dbName);
                skipped.Add(dbName);
            }
        }

        // 全部目标库被跳过 = 假成功陷阱，同缺失索引路径：不落空批次直接抛错
        if (targets.Count > 0 && skipped.Count == targets.Count)
            throw DbpilotInaccessibleDbsException.Create("索引使用率", skipped);

        // 空批次标记行（本批 0 条时推进批次时间）+ 分批落库 + 冷却刷新
        await PersistSnapshotsAsync(db, e.Id, rows, snapTime, LastUsageCollectUtc,
            () => new DbpilotIndexUsageSnapshot
            {
                InstanceId = e.Id,
                DbName = string.Empty,
                TableName = EmptyBatchMarker,
                IndexName = string.Empty,
                TypeDesc = string.Empty,
                SnapshotTime = snapTime,
            },
            "实例 {Id} 索引使用率快照落库 {Count} 条（{Time:u}）",
            e.Id, rows.Count, snapTime);
    }

    /// <summary>快照落库共用尾部（缺失索引/使用率）：分批追加 + 空批次标记行推进批次时间
    /// （否则"最新批次"永远停在最后一次非空采集，页面无法归零）+ 冷却窗口刷新；
    /// 标记行构造与日志模板由调用方传入。</summary>
    private static async Task PersistSnapshotsAsync<T>(
        DbContext db, int instanceId, List<T> rows, DateTime snapTime,
        ConcurrentDictionary<int, DateTime> lastCollect, Func<T> emptyRow,
        string logTemplate, params object[] logArgs)
    {
        for (var i = 0; i < rows.Count; i += SnapshotBatchSize)
            await db.InsertRangeAsync(rows.Skip(i).Take(SnapshotBatchSize).ToList());

        if (rows.Count == 0)
            await db.InsertAsync(emptyRow());

        lastCollect[instanceId] = snapTime;   // 任意采集路径（Job/手动）成功后刷新冷却窗口
        Log.Information(logTemplate, logArgs);
    }

    /// <summary>最新索引使用率快照（db 为空 = 全部库）。两步查询：先 MAX(snapshot_time) 再取该批明细，
    /// 避免全量载入历史行（全部索引 × 每日批次 × 90 天可达数十万行）。</summary>
    public async Task<ServiceResult<IndexUsageSnapshotResult>> GetUsageSnapshotAsync(int instanceId, string? db, bool excludeSystemDb = false)
    {
        var (err, entity) = await LoadInstanceAsync(instanceId);
        if (err != null) return ServiceResult<IndexUsageSnapshotResult>.Failed(err);

        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<IndexUsageSnapshotResult>.Failed(InstanceConfigResolver.DbNotConfigured);

        try
        {
            // db 过滤保留空批次标记行（否则按库过滤后"最新批次"退回旧数据）
            var marker = EmptyBatchMarker;
            var dbCond = db.IsNullOrEmpty() ? "" : " AND (db_name = @db OR table_name = @marker)";
            var args = db.IsNullOrEmpty() ? (object)new { instanceId, marker } : new { instanceId, db, marker };

            var latest = dbc.SqlQuery<DateTime?>(dialect.IndexUsageLatestSnapshotSql(dbCond), args).FirstOrDefault();

            var result = new IndexUsageSnapshotResult();
            if (latest is { } snapTime)
            {
                // Chloe DateTime 参数按 SQL datetime 3.33ms 网格发送：读回值可能偏移 ±1.67ms，
                // 等值匹配会落空 → ±5ms 邻域窗口（批次间隔至少秒级，不会跨批次）
                var from = snapTime.AddMilliseconds(-5);
                var to = snapTime.AddMilliseconds(5);
                var query = dbc.Query<DbpilotIndexUsageSnapshot>()
                    .Where(x => x.InstanceId == instanceId && x.SnapshotTime > from && x.SnapshotTime < to);
                if (!db.IsNullOrEmpty())
                    query = query.Where(x => x.DbName == db);
                var rows = await query.ToListAsync();

                // "全部库"视图排除系统库（页面开关；空批次标记行 db 为空串不受影响）
                if (excludeSystemDb && db.IsNullOrEmpty())
                    rows = rows.Where(x => !SystemDbs.Contains(x.DbName)).ToList();

                var online = entity!.Edition?.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) == true;
                result = BuildUsageSnapshotResult(rows, online, entity.Engine ?? DbpilotEngines.SqlServer);
            }

            // DMV 计数随实例重启清零：保留运行时长警示
            result.InstanceStartTimeUtc = SqlConverts.SpecifyUtc(entity!.SqlServerStartTime);
            result.DataIncomplete = IsDataIncomplete(entity.SqlServerStartTime);
            return ServiceResult<IndexUsageSnapshotResult>.Succeeded(result);
        }
        catch (Exception ex)
        {
            return ServiceResult<IndexUsageSnapshotResult>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>快照行 → 展示结果（纯函数，供单测）：排除标记行 → 计算 Action/脚本 → 按读次数降序 + 总览。
    /// engine 透传给 IndexScriptBuilder 选未使用处置脚本方言。</summary>
    internal static IndexUsageSnapshotResult BuildUsageSnapshotResult(
        List<DbpilotIndexUsageSnapshot> rows, bool onlineSupported, string engine = DbpilotEngines.SqlServer)
    {
        if (rows.Count == 0)
            return new IndexUsageSnapshotResult();

        var items = rows
            .Where(x => !IsUsageEmptyBatch(x))
            .Select(x =>
            {
                var action = x.AvgFragmentationPercent is { } f
                    ? IndexScriptBuilder.RecommendAction(f)
                    : FragAction.None;
                return new IndexUsageSnapshotItem
                {
                    DbName = x.DbName,
                    TableName = x.TableName,
                    IndexName = x.IndexName,
                    TypeDesc = x.TypeDesc,
                    IsPrimaryKey = x.IsPrimaryKey,
                    IsUnique = x.IsUnique,
                    KeyColumns = x.KeyColumns,
                    UserSeeks = x.UserSeeks,
                    UserScans = x.UserScans,
                    UserLookups = x.UserLookups,
                    UserUpdates = x.UserUpdates,
                    LastUserSeek = SqlConverts.SpecifyUtc(x.LastUserSeek),
                    LastUserScan = SqlConverts.SpecifyUtc(x.LastUserScan),
                    LastUserUpdate = SqlConverts.SpecifyUtc(x.LastUserUpdate),
                    UsedPageCount = x.UsedPageCount,
                    IsUnused = x.IsUnused,
                    AvgFragmentationPercent = x.AvgFragmentationPercent,
                    Action = action,
                    // 快照口径：多分区已归一 MAX，脚本不带 PARTITION 整索引处理；
                    // 页数未知（null，MySQL 无每索引页数）不算小表
                    SkipSmall = x.UsedPageCount is < IndexScriptBuilder.FragMinPagesNoAction,
                    FragScript = IndexScriptBuilder.BuildFragScript(x.TableName, x.IndexName, action, onlineSupported, partitionNumber: 1),
                    DisableScript = x.IsUnused
                        ? IndexScriptBuilder.BuildDisableSql(engine, x.TableName, x.IndexName)
                        : null,
                };
            })
            .OrderByDescending(x => x.UserSeeks + x.UserScans + x.UserLookups)
            .ToList();

        return new IndexUsageSnapshotResult
        {
            Items = items,
            Overview = BuildUsageOverview(items),
            SnapshotTimeUtc = DateTime.SpecifyKind(rows.Max(x => x.SnapshotTime), DateTimeKind.Utc),
        };
    }

    /// <summary>使用率总览统计（纯函数，供单测）：总量/总页数/空间 / 碎片&gt;30 / 低读 / 低读占比。</summary>
    internal static IndexUsageOverview BuildUsageOverview(List<IndexUsageSnapshotItem> items)
    {
        long Reads(IndexUsageSnapshotItem i) => i.UserSeeks + i.UserScans + i.UserLookups;

        return new IndexUsageOverview
        {
            Total = items.Count,
            TotalPages = items.Sum(x => x.UsedPageCount ?? 0),
            FragOver30Count = items.Count(x => (x.AvgFragmentationPercent ?? 0) > IndexScriptBuilder.FragRebuildThreshold),
            LowReadCount = items.Count(x => Reads(x) < 100),
            // 读占比 < 10%；分母（读+写）= 0 不计入
            LowReadRatioCount = items.Count(x => Reads(x) + x.UserUpdates > 0
                                                 && Reads(x) * 10 < Reads(x) + x.UserUpdates),
        };
    }

    /// <summary>索引空间变化趋势（每快照批次 used_page_count 求和，SQL 聚合；db 为空 = 全部库）。</summary>
    public async Task<ServiceResult<List<IndexUsageTrendPoint>>> GetUsageTrendAsync(int instanceId, string? db, bool excludeSystemDb = false)
    {
        var dbc = sp.GetService<DbContext>();
        if (dbc is null) return ServiceResult<List<IndexUsageTrendPoint>>.Failed(InstanceConfigResolver.DbNotConfigured);

        try
        {
            var marker = EmptyBatchMarker;
            var dbCond = db.IsNullOrEmpty() ? "" : " AND (db_name = @db OR table_name = @marker)";
            // "全部库"视图排除系统库（页面开关；db_name 为空串的空批次标记行不受影响）
            var sysCond = excludeSystemDb && db.IsNullOrEmpty()
                ? " AND db_name NOT IN (N'master', N'model', N'msdb', N'tempdb')"
                : "";
            var args = db.IsNullOrEmpty() ? (object)new { instanceId, marker } : new { instanceId, db, marker };

            var raw = dbc.SqlQuery<UsageTrendRow>(dialect.IndexUsageTrendSql(dbCond, sysCond), args).ToList();

            return ServiceResult<List<IndexUsageTrendPoint>>.Succeeded(BuildUsageTrend(raw));
        }
        catch (Exception ex)
        {
            return ServiceResult<List<IndexUsageTrendPoint>>.Failed($"查询失败：{ex.FriendlyMessage()}");
        }
    }

    /// <summary>趋势聚合行 → 展示点（纯函数，供单测）：UTC、升序、空批次点（SUM 为 NULL）保留为 0。</summary>
    internal static List<IndexUsageTrendPoint> BuildUsageTrend(List<UsageTrendRow> rows)
        => rows.Select(r => new IndexUsageTrendPoint
            {
                SnapshotTimeUtc = DateTime.SpecifyKind(r.SnapshotTime, DateTimeKind.Utc),
                TotalPages = r.TotalPages ?? 0,
            })
            .OrderBy(p => p.SnapshotTimeUtc)
            .ToList();

    /// <summary>空间趋势 SQL 聚合行（SqlQuery 映射用）。</summary>
    internal sealed class UsageTrendRow
    {
        public DateTime SnapshotTime { get; set; }
        public long? TotalPages { get; set; }
    }

    /// <summary>总览统计（对齐阿里云）：总量 / 提升&gt;80% / 近一天·一周·一月访问（按 last_user_seek）。</summary>
    public static MissingIndexOverview BuildOverview(List<MissingIndexItem> items)
    {
        var now = DateTime.UtcNow;
        int Count(Func<MissingIndexItem, bool> f) => items.Count(f);

        var total = items.Count;
        var high = Count(i => i.AvgUserImpact > 80);
        var day = Count(i => i.LastUserSeek >= now.AddDays(-1));
        var week = Count(i => i.LastUserSeek >= now.AddDays(-7));
        var month = Count(i => i.LastUserSeek >= now.AddDays(-30));

        return new MissingIndexOverview
        {
            Total = total,
            HighImpact = high,
            LastDayCount = day,
            LastWeekCount = week,
            LastMonthCount = month,
            HighImpactPercent = Percent(high, total),
            LastDayPercent = Percent(day, total),
            LastWeekPercent = Percent(week, total),
            LastMonthPercent = Percent(month, total),
        };

        static double Percent(int part, int total) => total == 0 ? 0 : Math.Round(part * 100.0 / total, 0);
    }

    /// <summary>目标库列表：db 非空 = 指定库；为空 = 全部非系统库。</summary>
    private async Task<List<string>> TargetDatabasesAsync(InstanceConfig cfg, string? db)
    {
        if (!db.IsNullOrEmpty())
            return [db];

        var all = await provider.GetDatabasesAsync(cfg);
        return all.Where(d => !SystemDbs.Contains(d)).ToList();
    }

    private static bool IsDataIncomplete(DateTime? instanceStartUtc)
        => instanceStartUtc is { } t
           && (DateTime.UtcNow - DateTime.SpecifyKind(t, DateTimeKind.Utc)).TotalDays < DataIncompleteDays;

    private async Task<(string? Error, DbpilotInstance? Entity)> LoadInstanceAsync(int id)
        => await InstanceConfigResolver.LoadAsync(sp, id);

    private InstanceConfig ToConfig(DbpilotInstance e)
        => InstanceConfigResolver.ToConfig(crypto, e);
}
