namespace DBPilot.Storage.Dialect;

/// <summary>
/// 平台库内联 SQL 的方言策略（B3）：Core 各查询/采集服务的原生 SQL 按引擎收敛于此，
/// 服务只拼条件片段（方言中立）并注入本接口取语句全文。
/// SQL Server 实现与收编前逐字节等价（B3a）；MySQL 实现为翻译版（B3b，TOP→LIMIT、
/// DATEADD 桶→DATE_FORMAT、@@ROWCOUNT→ROW_COUNT()、ESCAPE 反斜杠转义）。
/// </summary>
public interface IPlatformDialect
{
    // ── 死锁（DeadlockService）─────────────────────────────────────────────
    /// <summary>分页总数（condSql = 服务拼好的过滤条件，含前导 AND）。</summary>
    string DeadlockPageCountSql(string condSql);

    /// <summary>分页页行（SQL Server 2008 兼容 ROW_NUMBER 子查询 / MySQL 直接 LIMIT）。</summary>
    string DeadlockPageRowsSql(string condSql);

    /// <summary>过滤下拉：登录名 distinct。</summary>
    string DeadlockFilterLoginsSql();

    /// <summary>过滤下拉：主机名 distinct。</summary>
    string DeadlockFilterHostsSql();

    /// <summary>趋势-总数（unit = minute/hour/day，桶表达式方言化）。</summary>
    string DeadlockTrendTotalsSql(string unit);

    /// <summary>趋势-分色（锁类型 × 事件去重）。</summary>
    string DeadlockTrendColorsSql(string unit);

    /// <summary>相似归并 TOP：指纹 GROUP BY + 最近事件 ROW_NUMBER。</summary>
    string DeadlockFingerprintStatsSql();

    /// <summary>对象名模糊过滤条件片段（EXISTS + LIKE，ESCAPE 字符随方言）。</summary>
    string DeadlockObjectFilterSql();

    // ── 执行计划（QueryPlanQueryService）──────────────────────────────────
    /// <summary>指纹维度计划版本列表（CASE 算均值 + HasXml，端算不拉大列）。</summary>
    string PlanVersionsSql();

    /// <summary>该指纹变更事件（TOP 200 / LIMIT 200）。</summary>
    string PlanChangesTopSql();

    /// <summary>最近计划变更榜（JOIN 模板表取文本，TOP n）。</summary>
    string PlanChangeBoardSql(int n);

    // ── 慢 SQL（SlowSqlService）───────────────────────────────────────────
    /// <summary>模板聚合 Top 50（where = 服务拼好的条件，含 WHERE 前缀）。</summary>
    string SlowSqlTemplatesSql(string where);

    /// <summary>
    /// 降级模板榜 Top 50（无慢日志事件通道的引擎用）：dbpilot_top_sql_delta 按指纹聚合的近似视图，
    /// JOIN dbpilot_sql_template 取样例文本；where 作用于 d. 别名列（window_start 时间窗）。
    /// </summary>
    string SlowSqlTemplatesFromTopSqlSql(string where);

    /// <summary>趋势（条数/总耗时；conds = 服务拼好的附加条件，含前导 AND）。</summary>
    string SlowSqlTrendSql(string unit, string conds);

    // ── 索引使用率（IndexDiagnoseService）─────────────────────────────────
    /// <summary>最新快照时间（dbCond = 库过滤条件片段）。</summary>
    string IndexUsageLatestSnapshotSql(string dbCond);

    /// <summary>空间趋势聚合（dbCond/sysCond = 服务拼好的条件片段）。</summary>
    string IndexUsageTrendSql(string dbCond, string sysCond);

    // ── Top SQL（TopSqlService）───────────────────────────────────────────
    /// <summary>历史总榜（orderBy = 排序口径表达式；TOP n / LIMIT n）。</summary>
    string TopSqlHistorySql(string orderBy, int n);

    // ── Housekeeping（HousekeepingService）────────────────────────────────
    /// <summary>分批删除一批过期数据并带回受影响行数（DELETE TOP / DELETE LIMIT + ROWCOUNT）。</summary>
    string HousekeepingDeleteBatchSql(string table, string column, int batchSize);

    // ── 实例状态标注（SlowSqlCollectService）──────────────────────────────
    /// <summary>清空实例 last_error（成功回写）。</summary>
    string InstanceLastErrorClearSql();

    /// <summary>写入实例 last_error（失败标注，值不变时跳过）。</summary>
    string InstanceLastErrorSetSql();

    /// <summary>SQL 模板并发安全插入（dbpilot_sql_template，UNIQUE(instance_id, fingerprint)）：
    /// 已存在则无操作。TopSQL / 会话采样两 Job（或多宿主共享平台库）并发插同一指纹时
    /// "先查后插"存在竞态窗口（实测撞 uq_dbpilot_tpl）——各方言用原生原子 upsert 消除窗口；
    /// 尾部 SELECT 适配 Chloe SqlQuery（无非查询执行面）。参数：@instanceId/@fingerprint/@sqlText/@firstSeen/@lastSeen。</summary>
    string SqlTemplateUpsertSql();
}
