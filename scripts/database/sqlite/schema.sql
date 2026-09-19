-- DBPilot 平台库建表脚本（SQLite 3.25+，幂等：CREATE TABLE IF NOT EXISTS + CREATE INDEX IF NOT EXISTS）
-- 与 scripts/database/sqlserver/schema.sql、scripts/database/mysql/schema.sql 逐表逐列对应（V4.0 §6.9）
-- 由 SqliteSchemaInitializer 在启动时执行（EmbeddedResource，按分号切批）
-- 脚本约定：字符串与注释中一律不出现分号；表达式默认值须括号（如 DEFAULT (datetime('now'))，UTC 天然对齐）
-- 类型映射：VARCHAR/LONGTEXT/DATETIME→TEXT（时间列存 ISO8601 'YYYY-MM-DD HH:MM:SS.FFF' 文本，字典序=时间序）
--           INT/BIGINT/TINYINT→INTEGER、DECIMAL→NUMERIC、DOUBLE→REAL；自增 = INTEGER PRIMARY KEY AUTOINCREMENT

CREATE TABLE IF NOT EXISTS dbpilot_instance (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    name                    TEXT NOT NULL,
    host                    TEXT NOT NULL,
    port                    INTEGER NOT NULL DEFAULT 1433,
    engine                  TEXT NOT NULL DEFAULT 'sqlserver',  -- 引擎：sqlserver / mysql
    login_name              TEXT NOT NULL,
    password_cipher         TEXT NOT NULL,          -- AES-GCM（§5.7）
    enabled                 INTEGER NOT NULL DEFAULT 1,
    status                  INTEGER NOT NULL DEFAULT 0,  -- 0未知 1在线 2退避 3离线
    server_version          TEXT NULL,
    major_version           INTEGER NULL,           -- 10=2008, 10.5=2008R2, 11=2012...
    edition                 TEXT NULL,              -- R5 版别判断
    cpu_cores               INTEGER NULL,           -- vCores 参考线
    machine_name            TEXT NULL,
    sqlserver_start_time    TEXT NULL,              -- tempdb create_date，重启检测
    clock_skew_seconds      INTEGER NOT NULL DEFAULT 0,
    xe_file_path            TEXT NULL,              -- XE 文件目录
    slow_sql_threshold_ms   INTEGER NOT NULL DEFAULT 1000,
    blocking_threshold_sec  INTEGER NOT NULL DEFAULT 5,
    env_tag                 TEXT NULL,              -- 生产/测试，仅展示
    db_filter               TEXT NULL,              -- JSON：库白/黑名单
    last_error              TEXT NULL,
    last_heartbeat          TEXT NULL,
    create_time             TEXT NOT NULL DEFAULT (datetime('now')),
    update_time             TEXT NULL,
    CONSTRAINT uq_dbpilot_instance_name UNIQUE (name)
);

CREATE TABLE IF NOT EXISTS dbpilot_sql_template (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id   INTEGER NOT NULL,
    fingerprint   TEXT NOT NULL,      -- query_hash hex 或归一化文本哈希
    sql_text      TEXT NOT NULL,
    first_seen    TEXT NOT NULL DEFAULT (datetime('now')),
    last_seen     TEXT NOT NULL DEFAULT (datetime('now')),
    CONSTRAINT uq_dbpilot_tpl UNIQUE (instance_id, fingerprint)
);

CREATE TABLE IF NOT EXISTS dbpilot_active_request_sample (    -- 分钟聚合；10s 原始样本仅内存
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id           INTEGER NOT NULL,
    minute_time           TEXT NOT NULL,     -- 整分（UTC）
    sample_count          INTEGER NOT NULL,
    avg_active_sessions   NUMERIC(9,2) NOT NULL,
    max_active_sessions   INTEGER NOT NULL,
    buckets               TEXT NOT NULL,     -- {"cpu":0.21,"userIo":0.03,...}
    dims                  TEXT NOT NULL,
    create_time           TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_ars ON dbpilot_active_request_sample (instance_id, minute_time DESC);

CREATE TABLE IF NOT EXISTS dbpilot_top_sql_delta (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id          INTEGER NOT NULL,
    fingerprint          TEXT NOT NULL,
    db_name              TEXT NULL,             -- 计划编译库上下文（ad hoc 可能 NULL）；历史按库筛选
    window_start         TEXT NOT NULL,
    window_end           TEXT NOT NULL,
    exec_count           INTEGER NOT NULL,
    total_worker_ms      INTEGER NOT NULL,
    total_elapsed_ms     INTEGER NOT NULL,
    total_logical_reads  INTEGER NOT NULL,
    total_physical_reads INTEGER NOT NULL,
    total_writes         INTEGER NOT NULL,
    max_elapsed_ms       INTEGER NOT NULL,
    create_time          TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_tsd ON dbpilot_top_sql_delta (instance_id, window_start, fingerprint);

CREATE TABLE IF NOT EXISTS dbpilot_slow_sql (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id     INTEGER NOT NULL,
    event_time      TEXT NOT NULL,     -- 已按平台 UTC 校正
    db_name         TEXT NULL,
    login_name      TEXT NULL,
    host_name       TEXT NULL,
    app_name        TEXT NULL,
    session_id      INTEGER NULL,
    sql_type        INTEGER NOT NULL,  -- 1=rpc 2=batch
    duration_ms     INTEGER NOT NULL,
    cpu_ms          INTEGER NULL,
    logical_reads   INTEGER NULL,
    physical_reads  INTEGER NULL,
    writes          INTEGER NULL,
    row_count       INTEGER NULL,
    fingerprint     TEXT NULL,
    sql_text        TEXT NOT NULL,
    create_time     TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_ss ON dbpilot_slow_sql (instance_id, event_time, duration_ms DESC);

CREATE TABLE IF NOT EXISTS dbpilot_deadlock_event (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id        INTEGER NOT NULL,
    event_time         TEXT NOT NULL,
    victim_spids        TEXT NOT NULL,     -- 逗号分隔
    fingerprint        TEXT NOT NULL,      -- 图哈希，去重/相似归并
    deadlock_graph     TEXT NOT NULL,      -- 原始 XML 全文（事实源，详情重解析）
    utc_offset_minutes INTEGER NOT NULL DEFAULT 0,  -- 采集时实例本地-UTC（分钟）
    create_time        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_dl ON dbpilot_deadlock_event (instance_id, event_time DESC);

-- 死锁进程维度（一事件 N 行：SPID 行级明细 + 登录/主机维度的分析统计）
CREATE TABLE IF NOT EXISTS dbpilot_deadlock_process (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id          INTEGER NOT NULL,
    instance_id       INTEGER NOT NULL,
    event_time        TEXT NOT NULL,       -- 冗余：清理/统计免 JOIN
    spid              INTEGER NOT NULL,
    is_victim         INTEGER NOT NULL,
    login_name        TEXT NULL,
    host_name         TEXT NULL,
    client_app        TEXT NULL,
    isolation_level   TEXT NULL,
    lock_mode         TEXT NULL,
    wait_resource     TEXT NULL,
    status            TEXT NULL,
    transaction_name  TEXT NULL,
    log_used          INTEGER NOT NULL DEFAULT 0,   -- KB
    wait_time_ms      INTEGER NOT NULL DEFAULT 0,
    trancount         INTEGER NOT NULL DEFAULT 0,
    last_tran_started TEXT NULL,           -- UTC（采集时已按实例时区转换）
    input_buf         TEXT NULL,           -- 完整 SQL（inputbuf 全文）
    create_time       TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_dlp ON dbpilot_deadlock_process (instance_id, event_time DESC);
CREATE INDEX IF NOT EXISTS ix_dlp_event ON dbpilot_deadlock_process (event_id);

-- 死锁资源维度（一事件 M 行：锁类型趋势 / 对象维度分析）
CREATE TABLE IF NOT EXISTS dbpilot_deadlock_resource (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id      INTEGER NOT NULL,
    instance_id   INTEGER NOT NULL,
    event_time    TEXT NOT NULL,           -- 冗余：趋势 GROUP BY / 清理免 JOIN
    resource_type TEXT NOT NULL,           -- keylock/objectlock/pagelock/ridlock/exchangeEvent...
    object_name   TEXT NULL,
    index_name    TEXT NULL,
    lock_mode     TEXT NULL,
    create_time   TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_dlr ON dbpilot_deadlock_resource (instance_id, event_time DESC);
CREATE INDEX IF NOT EXISTS ix_dlr_event ON dbpilot_deadlock_resource (event_id);

CREATE TABLE IF NOT EXISTS dbpilot_blocking_event (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id      INTEGER NOT NULL,
    head_session_id  INTEGER NOT NULL,
    start_time       TEXT NOT NULL,
    end_time         TEXT NULL,
    blocked_count    INTEGER NOT NULL,
    max_wait_seconds INTEGER NOT NULL,
    head_info        TEXT NULL,
    head_db_name     TEXT NULL,         -- 头阻塞者所在库（留痕时定格；历史筛选用）
    chain_tree       TEXT NOT NULL,     -- 最近一次链路快照 JSON
    resolved         INTEGER NOT NULL DEFAULT 0,
    create_time      TEXT NOT NULL DEFAULT (datetime('now')),
    update_time      TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_bl ON dbpilot_blocking_event (instance_id, start_time DESC);

CREATE TABLE IF NOT EXISTS dbpilot_missing_index_snapshot (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id         INTEGER NOT NULL,
    db_name             TEXT NOT NULL,
    table_name          TEXT NOT NULL,
    equality_columns    TEXT NULL,
    inequality_columns  TEXT NULL,
    included_columns    TEXT NULL,
    user_seeks          INTEGER NOT NULL,
    avg_total_user_cost REAL NOT NULL,
    avg_user_impact     REAL NOT NULL,
    score               REAL NOT NULL,
    last_user_seek      TEXT NULL,
    table_pages         INTEGER NULL,
    table_rows          INTEGER NULL,
    create_index_sql    TEXT NOT NULL,
    snapshot_time       TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_mi ON dbpilot_missing_index_snapshot (instance_id, db_name, snapshot_time DESC);

CREATE TABLE IF NOT EXISTS dbpilot_index_usage_snapshot (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id     INTEGER NOT NULL,
    db_name         TEXT NOT NULL,
    table_name      TEXT NOT NULL,
    index_name      TEXT NOT NULL,
    is_primary_key  INTEGER NOT NULL,
    is_unique       INTEGER NOT NULL,
    type_desc       TEXT NOT NULL,
    user_seeks      INTEGER NOT NULL,
    user_scans      INTEGER NOT NULL,
    user_lookups    INTEGER NOT NULL,
    user_updates    INTEGER NOT NULL,
    last_user_seek  TEXT NULL,
    last_user_scan  TEXT NULL,
    key_columns     TEXT NULL,
    used_page_count INTEGER NULL,
    last_user_update TEXT NULL,
    avg_fragmentation_percent REAL NULL,
    is_unused       INTEGER NOT NULL DEFAULT 0,
    snapshot_time   TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_iu ON dbpilot_index_usage_snapshot (instance_id, db_name, snapshot_time DESC);

-- 查询计划快照：每 (实例, 指纹, plan_hash) 一行；累计值每拍刷新，XML 首见时抓一份
CREATE TABLE IF NOT EXISTS dbpilot_query_plan (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id          INTEGER NOT NULL,
    fingerprint          TEXT NOT NULL,      -- query_hash hex（与 top_sql_delta 同口径，NULL 兜底 sql_handle）
    db_name              TEXT NULL,
    query_plan_hash      TEXT NOT NULL,      -- 16 hex 小写
    plan_xml             TEXT NULL,          -- 首次发现时从缓存抓；>1MB 存 NULL（驱逐后无从补抓）
    compile_time_utc     TEXT NULL,          -- qs.creation_time（实例时区已换算 UTC）
    first_seen_utc       TEXT NOT NULL,
    last_seen_utc        TEXT NOT NULL,
    execution_count      INTEGER NOT NULL DEFAULT 0,   -- 该计划缓存累计，每拍刷新
    total_elapsed_ms     INTEGER NOT NULL DEFAULT 0,
    total_worker_ms      INTEGER NOT NULL DEFAULT 0,
    total_logical_reads  INTEGER NOT NULL DEFAULT 0,
    create_time          TEXT NOT NULL DEFAULT (datetime('now')),
    CONSTRAINT uq_dbpilot_qp UNIQUE (instance_id, fingerprint, query_plan_hash)
);
CREATE INDEX IF NOT EXISTS ix_qp_fp ON dbpilot_query_plan (instance_id, fingerprint);

-- 计划变更事件：同指纹出现新 plan_hash 时一条（老/新计划各自累计均值，检测时固化）
CREATE TABLE IF NOT EXISTS dbpilot_plan_change (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id       INTEGER NOT NULL,
    fingerprint       TEXT NOT NULL,
    db_name           TEXT NULL,
    old_plan_hash     TEXT NULL,
    new_plan_hash     TEXT NOT NULL,
    old_avg_elapsed_ms INTEGER NULL,             -- 老计划累计总量的均值（无老计划/除零为 NULL）
    new_avg_elapsed_ms INTEGER NULL,
    old_avg_worker_ms  INTEGER NULL,
    new_avg_worker_ms  INTEGER NULL,
    old_avg_reads      INTEGER NULL,
    new_avg_reads      INTEGER NULL,
    old_exec_count     INTEGER NULL,
    new_exec_count     INTEGER NULL,
    changed_at_utc    TEXT NOT NULL,             -- 新计划 compile_time（兜底采集时间）
    create_time       TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_pc_time ON dbpilot_plan_change (instance_id, changed_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_pc_fp ON dbpilot_plan_change (instance_id, fingerprint);

-- Top SQL 指纹黑名单（全局：query_hash 对相同脚本跨实例稳定；页面"排除"按钮写入）
CREATE TABLE IF NOT EXISTS dbpilot_top_sql_exclusion (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint TEXT NOT NULL,
    sql_head    TEXT NULL,
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tse ON dbpilot_top_sql_exclusion (fingerprint);

-- 实例性能指标分钟快照（MT-01 趋势页，10s 采集 / 保留 30 天）：
-- 瞬时列（cpu/mem/ple/命中率/连接/阻塞）直接填；率列 = 累计值相邻两拍差值÷间隔秒
CREATE TABLE IF NOT EXISTS dbpilot_instance_metrics (
    id                        INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id               INTEGER NOT NULL,
    sample_time               TEXT NOT NULL,
    cpu_usage_pct             NUMERIC(5,2) NULL,
    mem_usage_pct             NUMERIC(5,2) NULL,
    os_total_memory_kb        INTEGER NULL,
    os_available_memory_kb    INTEGER NULL,
    sql_memory_kb             INTEGER NULL,
    qps                       NUMERIC(12,2) NULL,
    tps                       NUMERIC(12,2) NULL,
    logins_sec                NUMERIC(12,2) NULL,
    compilations_sec          NUMERIC(12,2) NULL,
    recompilations_sec        NUMERIC(12,2) NULL,
    full_scans_sec            NUMERIC(12,2) NULL,
    lazy_writes_sec           NUMERIC(12,2) NULL,
    ple                       INTEGER NULL,
    buffer_cache_hit_ratio_pct NUMERIC(5,2) NULL,
    deadlocks_sec             NUMERIC(12,2) NULL,
    lock_timeouts_sec         NUMERIC(12,2) NULL,
    lock_waits_sec            NUMERIC(12,2) NULL,
    user_connections          INTEGER NULL,
    blocked_processes         INTEGER NULL,
    iops_read                 NUMERIC(12,2) NULL,
    iops_write                NUMERIC(12,2) NULL,
    mbps_read                 NUMERIC(10,3) NULL,
    mbps_write                NUMERIC(10,3) NULL
);
CREATE INDEX IF NOT EXISTS ix_im ON dbpilot_instance_metrics (instance_id, sample_time);

-- 磁盘卷用量快照（每卷一行）
CREATE TABLE IF NOT EXISTS dbpilot_instance_disk (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    instance_id         INTEGER NOT NULL,
    volume_mount_point  TEXT NOT NULL,
    total_mb            INTEGER NULL,
    available_mb        INTEGER NULL,
    used_pct            NUMERIC(5,2) NULL,
    sample_time         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_idisk ON dbpilot_instance_disk (instance_id, sample_time);
