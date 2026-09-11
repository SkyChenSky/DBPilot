-- DBPilot 平台库建表脚本（MySQL 8.0+，幂等：CREATE TABLE IF NOT EXISTS + 索引内联）
-- 与 scripts/database/sqlserver/schema.sql 逐表逐列对应（V4.0 §6.9）
-- 由 MySqlSchemaInitializer 在启动时执行（EmbeddedResource，按分号切批）
-- 脚本约定：字符串与注释中一律不出现分号；表达式默认值须括号（MySQL 8.0.13+，如 DEFAULT (UTC_TIMESTAMP(3))）

CREATE TABLE IF NOT EXISTS dbpilot_instance (
    id                      INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    name                    VARCHAR(100)  NOT NULL,
    host                    VARCHAR(256)  NOT NULL,
    port                    INT           NOT NULL DEFAULT 1433,
    engine                  VARCHAR(16)   NOT NULL DEFAULT 'sqlserver',  -- 引擎：sqlserver / mysql
    login_name              VARCHAR(64)   NOT NULL,
    password_cipher         VARCHAR(512)  NOT NULL,          -- AES-GCM（§5.7）
    enabled                 TINYINT(1)    NOT NULL DEFAULT 1,
    status                  INT           NOT NULL DEFAULT 0,  -- 0未知 1在线 2退避 3离线
    server_version          VARCHAR(32)   NULL,
    major_version           INT           NULL,              -- 10=2008, 10.5=2008R2, 11=2012...
    edition                 VARCHAR(64)   NULL,              -- R5 版别判断
    cpu_cores               INT           NULL,              -- vCores 参考线
    machine_name            VARCHAR(128)  NULL,
    sqlserver_start_time    DATETIME(3)   NULL,              -- tempdb create_date，重启检测
    clock_skew_seconds      INT           NOT NULL DEFAULT 0,
    xe_file_path            VARCHAR(256)  NULL,              -- XE 文件目录
    slow_sql_threshold_ms   INT           NOT NULL DEFAULT 1000,
    blocking_threshold_sec  INT           NOT NULL DEFAULT 5,
    env_tag                 VARCHAR(50)   NULL,              -- 生产/测试，仅展示
    db_filter               LONGTEXT      NULL,              -- JSON：库白/黑名单
    last_error              VARCHAR(500)  NULL,
    last_heartbeat          DATETIME(3)   NULL,
    create_time             DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    update_time             DATETIME(3)   NULL,
    CONSTRAINT uq_dbpilot_instance_name UNIQUE (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_sql_template (
    id            INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id   INT          NOT NULL,
    fingerprint   VARCHAR(64)  NOT NULL,      -- query_hash hex 或归一化文本哈希
    sql_text      LONGTEXT     NOT NULL,
    first_seen    DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    last_seen     DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    CONSTRAINT uq_dbpilot_tpl UNIQUE (instance_id, fingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_active_request_sample (    -- 分钟聚合；10s 原始样本仅内存
    id                    INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id           INT           NOT NULL,
    minute_time           DATETIME(3)   NOT NULL,   -- 整分（UTC）
    sample_count          INT           NOT NULL,
    avg_active_sessions   DECIMAL(9,2)  NOT NULL,
    max_active_sessions   INT           NOT NULL,
    buckets               LONGTEXT      NOT NULL,   -- {"cpu":0.21,"userIo":0.03,...}
    dims                  LONGTEXT      NOT NULL,
    create_time           DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_ars (instance_id, minute_time DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_top_sql_delta (
    id                   INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id          INT          NOT NULL,
    fingerprint          VARCHAR(64)  NOT NULL,
    db_name              VARCHAR(128) NULL,             -- 计划编译库上下文（ad hoc 可能 NULL）；历史按库筛选
    window_start         DATETIME(3)  NOT NULL,
    window_end           DATETIME(3)  NOT NULL,
    exec_count           BIGINT       NOT NULL,
    total_worker_ms      BIGINT       NOT NULL,
    total_elapsed_ms     BIGINT       NOT NULL,
    total_logical_reads  BIGINT       NOT NULL,
    total_physical_reads BIGINT       NOT NULL,
    total_writes         BIGINT       NOT NULL,
    max_elapsed_ms       BIGINT       NOT NULL,
    create_time          DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_tsd (instance_id, window_start, fingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_slow_sql (
    id              INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id     INT           NOT NULL,
    event_time      DATETIME(3)   NOT NULL,     -- 已按平台 UTC 校正
    db_name         VARCHAR(128)  NULL,
    login_name      VARCHAR(128)  NULL,
    host_name       VARCHAR(128)  NULL,
    app_name        VARCHAR(256)  NULL,
    session_id      INT           NULL,
    sql_type        INT           NOT NULL,     -- 1=rpc 2=batch
    duration_ms     BIGINT        NOT NULL,
    cpu_ms          BIGINT        NULL,
    logical_reads   BIGINT        NULL,
    physical_reads  BIGINT        NULL,
    writes          BIGINT        NULL,
    row_count       BIGINT        NULL,
    fingerprint     VARCHAR(64)   NULL,
    sql_text        LONGTEXT      NOT NULL,
    create_time     DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_ss (instance_id, event_time, duration_ms DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_deadlock_event (
    id                 INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id        INT           NOT NULL,
    event_time         DATETIME(3)   NOT NULL,
    victim_spids        VARCHAR(100)  NOT NULL,     -- 逗号分隔
    fingerprint        VARCHAR(64)   NOT NULL,      -- 图哈希，去重/相似归并
    deadlock_graph     LONGTEXT      NOT NULL,      -- 原始 XML 全文（事实源，详情重解析）
    utc_offset_minutes INT           NOT NULL DEFAULT 0,  -- 采集时实例本地-UTC（分钟）
    create_time        DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_dl (instance_id, event_time DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 死锁进程维度（一事件 N 行：SPID 行级明细 + 登录/主机维度的分析统计）
CREATE TABLE IF NOT EXISTS dbpilot_deadlock_process (
    id                INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    event_id          INT           NOT NULL,
    instance_id       INT           NOT NULL,
    event_time        DATETIME(3)   NOT NULL,       -- 冗余：清理/统计免 JOIN
    spid              INT           NOT NULL,
    is_victim         TINYINT(1)    NOT NULL,
    login_name        VARCHAR(200)  NULL,
    host_name         VARCHAR(100)  NULL,
    client_app        VARCHAR(200)  NULL,
    isolation_level   VARCHAR(60)   NULL,
    lock_mode         VARCHAR(10)   NULL,
    wait_resource     VARCHAR(500)  NULL,
    status            VARCHAR(30)   NULL,
    transaction_name  VARCHAR(60)   NULL,
    log_used          INT           NOT NULL DEFAULT 0,   -- KB
    wait_time_ms      INT           NOT NULL DEFAULT 0,
    trancount         INT           NOT NULL DEFAULT 0,
    last_tran_started DATETIME(3)   NULL,           -- UTC（采集时已按实例时区转换）
    input_buf         LONGTEXT      NULL,           -- 完整 SQL（inputbuf 全文）
    create_time       DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_dlp (instance_id, event_time DESC),
    INDEX ix_dlp_event (event_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 死锁资源维度（一事件 M 行：锁类型趋势 / 对象维度分析）
CREATE TABLE IF NOT EXISTS dbpilot_deadlock_resource (
    id            INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    event_id      INT           NOT NULL,
    instance_id   INT           NOT NULL,
    event_time    DATETIME(3)   NOT NULL,           -- 冗余：趋势 GROUP BY / 清理免 JOIN
    resource_type VARCHAR(30)   NOT NULL,           -- keylock/objectlock/pagelock/ridlock/exchangeEvent...
    object_name   VARCHAR(400)  NULL,
    index_name    VARCHAR(200)  NULL,
    lock_mode     VARCHAR(10)   NULL,
    create_time   DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_dlr (instance_id, event_time DESC),
    INDEX ix_dlr_event (event_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_blocking_event (
    id               INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id      INT          NOT NULL,
    head_session_id  INT          NOT NULL,
    start_time       DATETIME(3)  NOT NULL,
    end_time         DATETIME(3)  NULL,
    blocked_count    INT          NOT NULL,
    max_wait_seconds INT          NOT NULL,
    head_info        LONGTEXT     NULL,
    head_db_name     VARCHAR(128) NULL,         -- 头阻塞者所在库（留痕时定格；历史筛选用）
    chain_tree       LONGTEXT     NOT NULL,     -- 最近一次链路快照 JSON
    resolved         TINYINT(1)   NOT NULL DEFAULT 0,
    create_time      DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    update_time      DATETIME(3)  NULL,
    INDEX ix_bl (instance_id, start_time DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_missing_index_snapshot (
    id                  INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id         INT            NOT NULL,
    db_name             VARCHAR(128)   NOT NULL,
    table_name          VARCHAR(256)   NOT NULL,
    equality_columns    LONGTEXT       NULL,
    inequality_columns  LONGTEXT       NULL,
    included_columns    LONGTEXT       NULL,
    user_seeks          BIGINT         NOT NULL,
    avg_total_user_cost DOUBLE         NOT NULL,
    avg_user_impact     DOUBLE         NOT NULL,
    score               DOUBLE         NOT NULL,
    last_user_seek      DATETIME(3)    NULL,
    table_pages         INT            NULL,
    table_rows          BIGINT         NULL,
    create_index_sql    LONGTEXT       NOT NULL,
    snapshot_time       DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_mi (instance_id, db_name, snapshot_time DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS dbpilot_index_usage_snapshot (
    id              INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id     INT            NOT NULL,
    db_name         VARCHAR(128)   NOT NULL,
    table_name      VARCHAR(256)   NOT NULL,
    index_name      VARCHAR(256)   NOT NULL,
    is_primary_key  TINYINT(1)     NOT NULL,
    is_unique       TINYINT(1)     NOT NULL,
    type_desc       VARCHAR(60)    NOT NULL,
    user_seeks      BIGINT         NOT NULL,
    user_scans      BIGINT         NOT NULL,
    user_lookups    BIGINT         NOT NULL,
    user_updates    BIGINT         NOT NULL,
    last_user_seek  DATETIME(3)    NULL,
    last_user_scan  DATETIME(3)    NULL,
    key_columns     LONGTEXT       NULL,
    used_page_count BIGINT         NULL,
    last_user_update DATETIME(3)   NULL,
    avg_fragmentation_percent DOUBLE NULL,
    is_unused       TINYINT(1)     NOT NULL DEFAULT 0,
    snapshot_time   DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_iu (instance_id, db_name, snapshot_time DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 查询计划快照：每 (实例, 指纹, plan_hash) 一行；累计值每拍刷新，XML 首见时抓一份
CREATE TABLE IF NOT EXISTS dbpilot_query_plan (
    id                   BIGINT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id          INT           NOT NULL,
    fingerprint          VARCHAR(64)   NOT NULL,      -- query_hash hex（与 top_sql_delta 同口径，NULL 兜底 sql_handle）
    db_name              VARCHAR(128)  NULL,
    query_plan_hash      VARCHAR(32)   NOT NULL,      -- 16 hex 小写
    plan_xml             LONGTEXT      NULL,          -- 首次发现时从缓存抓；>1MB 存 NULL（驱逐后无从补抓）
    compile_time_utc     DATETIME(3)   NULL,          -- qs.creation_time（实例时区已换算 UTC）
    first_seen_utc       DATETIME(3)   NOT NULL,
    last_seen_utc        DATETIME(3)   NOT NULL,
    execution_count      BIGINT        NOT NULL DEFAULT 0,   -- 该计划缓存累计，每拍刷新
    total_elapsed_ms     BIGINT        NOT NULL DEFAULT 0,
    total_worker_ms      BIGINT        NOT NULL DEFAULT 0,
    total_logical_reads  BIGINT        NOT NULL DEFAULT 0,
    create_time          DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    CONSTRAINT uq_dbpilot_qp UNIQUE (instance_id, fingerprint, query_plan_hash),
    INDEX ix_qp_fp (instance_id, fingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 计划变更事件：同指纹出现新 plan_hash 时一条（老/新计划各自累计均值，检测时固化）
CREATE TABLE IF NOT EXISTS dbpilot_plan_change (
    id                BIGINT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id       INT           NOT NULL,
    fingerprint       VARCHAR(64)   NOT NULL,
    db_name           VARCHAR(128)  NULL,
    old_plan_hash     VARCHAR(32)   NULL,
    new_plan_hash     VARCHAR(32)   NOT NULL,
    old_avg_elapsed_ms BIGINT       NULL,             -- 老计划累计总量的均值（无老计划/除零为 NULL）
    new_avg_elapsed_ms BIGINT       NULL,
    old_avg_worker_ms  BIGINT       NULL,
    new_avg_worker_ms  BIGINT       NULL,
    old_avg_reads      BIGINT       NULL,
    new_avg_reads      BIGINT       NULL,
    old_exec_count     BIGINT       NULL,
    new_exec_count     BIGINT       NULL,
    changed_at_utc    DATETIME(3)   NOT NULL,         -- 新计划 compile_time（兜底采集时间）
    create_time       DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    INDEX ix_pc_time (instance_id, changed_at_utc DESC),
    INDEX ix_pc_fp (instance_id, fingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- Top SQL 指纹黑名单（全局：query_hash 对相同脚本跨实例稳定；页面"排除"按钮写入）
CREATE TABLE IF NOT EXISTS dbpilot_top_sql_exclusion (
    id          INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    fingerprint VARCHAR(64)   NOT NULL,
    sql_head    VARCHAR(500)  NULL,
    created_at  DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
    UNIQUE INDEX ux_tse (fingerprint)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 实例性能指标分钟快照（MT-01 趋势页，10s 采集 / 保留 30 天）：
-- 瞬时列（cpu/mem/ple/命中率/连接/阻塞）直接填；率列 = 累计值相邻两拍差值÷间隔秒
CREATE TABLE IF NOT EXISTS dbpilot_instance_metrics (
    id                        BIGINT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id               INT           NOT NULL,
    sample_time               DATETIME(3)   NOT NULL,
    cpu_usage_pct             DECIMAL(5,2)  NULL,
    mem_usage_pct             DECIMAL(5,2)  NULL,
    os_total_memory_kb        BIGINT        NULL,
    os_available_memory_kb    BIGINT        NULL,
    sql_memory_kb             BIGINT        NULL,
    qps                       DECIMAL(12,2) NULL,
    tps                       DECIMAL(12,2) NULL,
    logins_sec                DECIMAL(12,2) NULL,
    compilations_sec          DECIMAL(12,2) NULL,
    recompilations_sec        DECIMAL(12,2) NULL,
    full_scans_sec            DECIMAL(12,2) NULL,
    lazy_writes_sec           DECIMAL(12,2) NULL,
    ple                       INT           NULL,
    buffer_cache_hit_ratio_pct DECIMAL(5,2) NULL,
    deadlocks_sec             DECIMAL(12,2) NULL,
    lock_timeouts_sec         DECIMAL(12,2) NULL,
    lock_waits_sec            DECIMAL(12,2) NULL,
    user_connections          INT           NULL,
    blocked_processes         INT           NULL,
    iops_read                 DECIMAL(12,2) NULL,
    iops_write                DECIMAL(12,2) NULL,
    mbps_read                 DECIMAL(10,3) NULL,
    mbps_write                DECIMAL(10,3) NULL,
    INDEX ix_im (instance_id, sample_time)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- 磁盘卷用量快照（每卷一行）
CREATE TABLE IF NOT EXISTS dbpilot_instance_disk (
    id                  BIGINT NOT NULL PRIMARY KEY AUTO_INCREMENT,
    instance_id         INT           NOT NULL,
    volume_mount_point  VARCHAR(260)  NOT NULL,
    total_mb            BIGINT        NULL,
    available_mb        BIGINT        NULL,
    used_pct            DECIMAL(5,2)  NULL,
    sample_time         DATETIME(3)   NOT NULL,
    INDEX ix_idisk (instance_id, sample_time)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
