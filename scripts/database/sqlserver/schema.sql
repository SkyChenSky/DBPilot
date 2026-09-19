-- DBPilot 平台库建表脚本（幂等，SQL Server 2008 兼容）
-- 对应设计文档 V4.0 §6.9；由 SchemaInitializer 在启动时执行（EmbeddedResource）。

IF OBJECT_ID(N'dbpilot_instance') IS NULL
CREATE TABLE dbpilot_instance (
    id                      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name                    NVARCHAR(100)  NOT NULL,
    host                    NVARCHAR(256)  NOT NULL,
    port                    INT            NOT NULL CONSTRAINT df_ins_port DEFAULT (1433),
    engine                  VARCHAR(16)    NOT NULL CONSTRAINT df_ins_engine DEFAULT ('sqlserver'),  -- 引擎：sqlserver / mysql
    login_name              NVARCHAR(64)   NOT NULL,
    password_cipher         NVARCHAR(512)  NOT NULL,          -- AES-GCM（§5.7）
    enabled                 BIT            NOT NULL CONSTRAINT df_ins_en DEFAULT (1),
    status                  INT            NOT NULL CONSTRAINT df_ins_st DEFAULT (0),  -- 0未知 1在线 2退避 3离线
    server_version          NVARCHAR(32)   NULL,
    major_version           INT            NULL,              -- 10=2008, 10.5=2008R2, 11=2012...
    edition                 NVARCHAR(64)   NULL,              -- R5 版别判断
    cpu_cores               INT            NULL,              -- vCores 参考线
    machine_name            NVARCHAR(128)  NULL,
    sqlserver_start_time    DATETIME2(3)   NULL,              -- tempdb create_date，重启检测
    clock_skew_seconds      INT            NOT NULL CONSTRAINT df_ins_skew DEFAULT (0),
    xe_file_path            NVARCHAR(256)  NULL,              -- XE 文件目录
    slow_sql_threshold_ms   INT            NOT NULL CONSTRAINT df_ins_slow DEFAULT (1000),
    blocking_threshold_sec  INT            NOT NULL CONSTRAINT df_ins_blk DEFAULT (5),
    env_tag                 NVARCHAR(50)   NULL,              -- 生产/测试，仅展示
    db_filter               NVARCHAR(MAX)  NULL,              -- JSON：库白/黑名单
    last_error              NVARCHAR(500)  NULL,
    last_heartbeat          DATETIME2(3)   NULL,
    create_time             DATETIME2(3)   NOT NULL CONSTRAINT df_ins_ct DEFAULT (SYSDATETIME()),
    update_time             DATETIME2(3)   NULL,
    CONSTRAINT uq_dbpilot_instance_name UNIQUE (name)
);
GO

IF OBJECT_ID(N'dbpilot_sql_template') IS NULL
CREATE TABLE dbpilot_sql_template (
    id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id   INT           NOT NULL,
    fingerprint   NVARCHAR(64)  NOT NULL,      -- query_hash hex 或归一化文本哈希
    sql_text      NVARCHAR(MAX) NOT NULL,
    first_seen    DATETIME2(3)  NOT NULL CONSTRAINT df_tpl_fs DEFAULT (SYSDATETIME()),
    last_seen     DATETIME2(3)  NOT NULL CONSTRAINT df_tpl_ls DEFAULT (SYSDATETIME()),
    CONSTRAINT uq_dbpilot_tpl UNIQUE (instance_id, fingerprint)
);
GO

IF OBJECT_ID(N'dbpilot_active_request_sample') IS NULL
CREATE TABLE dbpilot_active_request_sample (    -- 分钟聚合；10s 原始样本仅内存
    id                    INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id           INT            NOT NULL,
    minute_time           DATETIME2(3)   NOT NULL,   -- 整分（UTC）
    sample_count          INT            NOT NULL,
    avg_active_sessions   NUMERIC(9,2)   NOT NULL,
    max_active_sessions   INT            NOT NULL,
    buckets               NVARCHAR(MAX)  NOT NULL,   -- {"cpu":0.21,"userIo":0.03,...}
    dims                  NVARCHAR(MAX)  NOT NULL,   -- {"sql":[...],"wait":[...],"user":[...],"host":[...],"command":[...],"db":[...],"status":[...]}
    create_time           DATETIME2(3)   NOT NULL CONSTRAINT df_ars_ct DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID(N'dbpilot_top_sql_delta') IS NULL
CREATE TABLE dbpilot_top_sql_delta (
    id                   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id          INT           NOT NULL,
    fingerprint          NVARCHAR(64)  NOT NULL,
    db_name              NVARCHAR(128) NULL,             -- 计划编译库上下文（ad hoc 可能 NULL）；历史按库筛选
    window_start         DATETIME2(3)  NOT NULL,
    window_end           DATETIME2(3)  NOT NULL,
    exec_count           BIGINT        NOT NULL,
    total_worker_ms      BIGINT        NOT NULL,
    total_elapsed_ms     BIGINT        NOT NULL,
    total_logical_reads  BIGINT        NOT NULL,
    total_physical_reads BIGINT        NOT NULL,
    total_writes         BIGINT        NOT NULL,
    max_elapsed_ms       BIGINT        NOT NULL,
    create_time          DATETIME2(3)  NOT NULL CONSTRAINT df_tsd_ct DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID(N'dbpilot_slow_sql') IS NULL
CREATE TABLE dbpilot_slow_sql (
    id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id     INT           NOT NULL,
    event_time      DATETIME2(3)  NOT NULL,     -- 已按平台 UTC 校正
    db_name         NVARCHAR(128)  NULL,
    login_name      NVARCHAR(128)  NULL,
    host_name       NVARCHAR(128)  NULL,
    app_name        NVARCHAR(256)  NULL,
    session_id      INT            NULL,
    sql_type        INT            NOT NULL,     -- 1=rpc 2=batch
    duration_ms     BIGINT         NOT NULL,
    cpu_ms          BIGINT         NULL,
    logical_reads   BIGINT         NULL,
    physical_reads  BIGINT         NULL,
    writes          BIGINT         NULL,
    row_count       BIGINT         NULL,
    fingerprint     NVARCHAR(64)   NULL,
    sql_text        NVARCHAR(MAX) NOT NULL,
    create_time     DATETIME2(3)  NOT NULL CONSTRAINT df_ss_ct DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID(N'dbpilot_deadlock_event') IS NULL
CREATE TABLE dbpilot_deadlock_event (
    id                 INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id        INT           NOT NULL,
    event_time         DATETIME2(3)  NOT NULL,
    victim_spids       NVARCHAR(100)  NOT NULL,     -- 逗号分隔
    fingerprint        NVARCHAR(64)  NOT NULL,      -- 图哈希，去重/相似归并
    deadlock_graph     NVARCHAR(MAX) NOT NULL,      -- 原始 XML 全文（事实源，详情重解析）
    utc_offset_minutes INT           NOT NULL CONSTRAINT df_dl_off DEFAULT (0),  -- 采集时实例本地-UTC（分钟）
    create_time        DATETIME2(3)  NOT NULL CONSTRAINT df_dl_ct DEFAULT (SYSDATETIME())
);
GO

-- 死锁进程维度（一事件 N 行：SPID 行级明细 + 登录/主机维度的分析统计）
IF OBJECT_ID(N'dbpilot_deadlock_process') IS NULL
CREATE TABLE dbpilot_deadlock_process (
    id                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    event_id          INT           NOT NULL,
    instance_id       INT           NOT NULL,
    event_time        DATETIME2(3)  NOT NULL,       -- 冗余：清理/统计免 JOIN
    spid              INT           NOT NULL,
    is_victim         BIT           NOT NULL,
    login_name        NVARCHAR(200) NULL,
    host_name         NVARCHAR(100) NULL,
    client_app        NVARCHAR(200) NULL,
    isolation_level   NVARCHAR(60)  NULL,
    lock_mode         NVARCHAR(10)  NULL,
    wait_resource     NVARCHAR(500) NULL,
    status            NVARCHAR(30)  NULL,
    transaction_name  NVARCHAR(60)  NULL,
    log_used          INT           NOT NULL CONSTRAINT df_dlp_lu DEFAULT (0),   -- KB
    wait_time_ms      INT           NOT NULL CONSTRAINT df_dlp_wt DEFAULT (0),
    trancount         INT           NOT NULL CONSTRAINT df_dlp_tc DEFAULT (0),
    last_tran_started DATETIME2(3)  NULL,           -- UTC（采集时已按实例时区转换）
    input_buf         NVARCHAR(MAX) NULL,           -- 完整 SQL（inputbuf 全文）
    create_time       DATETIME2(3)  NOT NULL CONSTRAINT df_dlp_ct DEFAULT (SYSDATETIME())
);
GO

-- 死锁资源维度（一事件 M 行：锁类型趋势 / 对象维度分析）
IF OBJECT_ID(N'dbpilot_deadlock_resource') IS NULL
CREATE TABLE dbpilot_deadlock_resource (
    id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    event_id      INT           NOT NULL,
    instance_id   INT           NOT NULL,
    event_time    DATETIME2(3)  NOT NULL,           -- 冗余：趋势 GROUP BY / 清理免 JOIN
    resource_type NVARCHAR(30)  NOT NULL,           -- keylock/objectlock/pagelock/ridlock/exchangeEvent...
    object_name   NVARCHAR(400) NULL,
    index_name    NVARCHAR(200) NULL,
    lock_mode     NVARCHAR(10)  NULL,
    create_time   DATETIME2(3)  NOT NULL CONSTRAINT df_dlr_ct DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID(N'dbpilot_blocking_event') IS NULL
CREATE TABLE dbpilot_blocking_event (
    id               INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id      INT           NOT NULL,
    head_session_id  INT           NOT NULL,
    start_time       DATETIME2(3)  NOT NULL,
    end_time         DATETIME2(3)  NULL,
    blocked_count    INT           NOT NULL,
    max_wait_seconds INT           NOT NULL,
    head_info        NVARCHAR(MAX) NULL,
    head_db_name     NVARCHAR(128) NULL,         -- 头阻塞者所在库（留痕时定格；历史筛选用）
    chain_tree       NVARCHAR(MAX) NOT NULL,     -- 最近一次链路快照 JSON
    resolved         BIT           NOT NULL CONSTRAINT df_bl_r DEFAULT (0),
    create_time      DATETIME2(3)  NOT NULL CONSTRAINT df_bl_ct DEFAULT (SYSDATETIME()),
    update_time      DATETIME2(3)  NULL
);
GO

IF OBJECT_ID(N'dbpilot_missing_index_snapshot') IS NULL
CREATE TABLE dbpilot_missing_index_snapshot (
    id                  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id         INT            NOT NULL,
    db_name             NVARCHAR(128)  NOT NULL,
    table_name          NVARCHAR(256)  NOT NULL,
    equality_columns    NVARCHAR(MAX)  NULL,
    inequality_columns  NVARCHAR(MAX)  NULL,
    included_columns    NVARCHAR(MAX)  NULL,
    user_seeks          BIGINT         NOT NULL,
    avg_total_user_cost FLOAT          NOT NULL,
    avg_user_impact     FLOAT          NOT NULL,
    score               FLOAT          NOT NULL,
    last_user_seek      DATETIME2(3)  NULL,
    table_pages         INT            NULL,
    table_rows          BIGINT         NULL,
    create_index_sql    NVARCHAR(MAX) NOT NULL,
    snapshot_time       DATETIME2(3)  NOT NULL CONSTRAINT df_mi_ct DEFAULT (SYSDATETIME())
);
GO

IF OBJECT_ID(N'dbpilot_index_usage_snapshot') IS NULL
CREATE TABLE dbpilot_index_usage_snapshot (
    id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id     INT           NOT NULL,
    db_name         NVARCHAR(128)  NOT NULL,
    table_name      NVARCHAR(256)  NOT NULL,
    index_name      NVARCHAR(256)  NOT NULL,
    is_primary_key  BIT           NOT NULL,
    is_unique       BIT           NOT NULL,
    type_desc       NVARCHAR(60)  NOT NULL,
    user_seeks      BIGINT        NOT NULL,
    user_scans      BIGINT        NOT NULL,
    user_lookups    BIGINT        NOT NULL,
    user_updates    BIGINT        NOT NULL,
    last_user_seek  DATETIME2(3)  NULL,
    last_user_scan  DATETIME2(3)  NULL,
    key_columns     NVARCHAR(MAX) NULL,
    used_page_count BIGINT        NULL,
    last_user_update DATETIME2(3) NULL,
    avg_fragmentation_percent FLOAT NULL,
    is_unused       BIT           NOT NULL CONSTRAINT df_iu_unused DEFAULT (0),
    snapshot_time   DATETIME2(3)  NOT NULL CONSTRAINT df_iu_ct DEFAULT (SYSDATETIME())
);
GO

-- 查询计划快照：每 (实例, 指纹, plan_hash) 一行；累计值每拍刷新，XML 首见时抓一份
IF OBJECT_ID(N'dbpilot_query_plan') IS NULL
CREATE TABLE dbpilot_query_plan (
    id                   BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id          INT           NOT NULL,
    fingerprint          NVARCHAR(64)  NOT NULL,      -- query_hash hex（与 top_sql_delta 同口径，NULL 兜底 sql_handle）
    db_name              NVARCHAR(128) NULL,
    query_plan_hash      VARCHAR(32)   NOT NULL,      -- 16 hex 小写
    plan_xml             NVARCHAR(MAX) NULL,          -- 首次发现时从缓存抓；>1MB 存 NULL（驱逐后无从补抓）
    compile_time_utc     DATETIME2(3)  NULL,          -- qs.creation_time（实例时区已换算 UTC）
    first_seen_utc       DATETIME2(3)  NOT NULL,
    last_seen_utc        DATETIME2(3)  NOT NULL,
    execution_count      BIGINT        NOT NULL CONSTRAINT df_qp_ec DEFAULT (0),   -- 该计划缓存累计，每拍刷新
    total_elapsed_ms     BIGINT        NOT NULL CONSTRAINT df_qp_el DEFAULT (0),
    total_worker_ms      BIGINT        NOT NULL CONSTRAINT df_qp_wk DEFAULT (0),
    total_logical_reads  BIGINT        NOT NULL CONSTRAINT df_qp_rd DEFAULT (0),
    create_time          DATETIME2(3)  NOT NULL CONSTRAINT df_qp_ct DEFAULT (SYSDATETIME()),
    CONSTRAINT uq_dbpilot_qp UNIQUE (instance_id, fingerprint, query_plan_hash)
);
GO

-- 计划变更事件：同指纹出现新 plan_hash 时一条（老/新计划各自累计均值，检测时固化）
IF OBJECT_ID(N'dbpilot_plan_change') IS NULL
CREATE TABLE dbpilot_plan_change (
    id                BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id       INT           NOT NULL,
    fingerprint       NVARCHAR(64)  NOT NULL,
    db_name           NVARCHAR(128) NULL,
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
    changed_at_utc    DATETIME2(3)  NOT NULL,         -- 新计划 compile_time（兜底采集时间）
    create_time       DATETIME2(3)  NOT NULL CONSTRAINT df_pc_ct DEFAULT (SYSDATETIME())
);
GO

-- Top SQL 指纹黑名单（全局：query_hash 对相同脚本跨实例稳定；页面"排除"按钮写入）
IF OBJECT_ID(N'dbpilot_top_sql_exclusion') IS NULL
CREATE TABLE dbpilot_top_sql_exclusion (
    id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    fingerprint VARCHAR(64)     NOT NULL,
    sql_head    NVARCHAR(500)   NULL,
    created_at  DATETIME2(3)   NOT NULL CONSTRAINT df_tse_ct DEFAULT (SYSDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_tse')
    CREATE UNIQUE INDEX ux_tse ON dbpilot_top_sql_exclusion (fingerprint);
GO

-- 索引（幂等）
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_ars')
    CREATE INDEX ix_ars ON dbpilot_active_request_sample (instance_id, minute_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsd')
    CREATE INDEX ix_tsd ON dbpilot_top_sql_delta (instance_id, window_start, fingerprint);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_ss')
    CREATE INDEX ix_ss ON dbpilot_slow_sql (instance_id, event_time, duration_ms DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dl')
    CREATE INDEX ix_dl ON dbpilot_deadlock_event (instance_id, event_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dlp')
    CREATE INDEX ix_dlp ON dbpilot_deadlock_process (instance_id, event_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dlp_event')
    CREATE INDEX ix_dlp_event ON dbpilot_deadlock_process (event_id);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dlr')
    CREATE INDEX ix_dlr ON dbpilot_deadlock_resource (instance_id, event_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dlr_event')
    CREATE INDEX ix_dlr_event ON dbpilot_deadlock_resource (event_id);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_bl')
    CREATE INDEX ix_bl ON dbpilot_blocking_event (instance_id, start_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_mi')
    CREATE INDEX ix_mi ON dbpilot_missing_index_snapshot (instance_id, db_name, snapshot_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_iu')
    CREATE INDEX ix_iu ON dbpilot_index_usage_snapshot (instance_id, db_name, snapshot_time DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_qp_fp')
    CREATE INDEX ix_qp_fp ON dbpilot_query_plan (instance_id, fingerprint);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_time')
    CREATE INDEX ix_pc_time ON dbpilot_plan_change (instance_id, changed_at_utc DESC);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_fp')
    CREATE INDEX ix_pc_fp ON dbpilot_plan_change (instance_id, fingerprint);
GO

-- 实例性能指标分钟快照（MT-01 趋势页，60s 采集 / 保留 30 天）：
-- 瞬时列（cpu/mem/ple/命中率/连接/阻塞）直接填；率列 = dm_os_performance_counters 等累计值相邻两拍差值÷间隔秒
IF OBJECT_ID(N'dbpilot_instance_metrics') IS NULL
CREATE TABLE dbpilot_instance_metrics (
    id                        BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id               INT           NOT NULL,
    sample_time               DATETIME2(3)  NOT NULL,
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
    mbps_write                DECIMAL(10,3) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_im')
    CREATE INDEX ix_im ON dbpilot_instance_metrics (instance_id, sample_time);
GO

-- 磁盘卷用量快照（每卷一行；2008 无 dm_os_volume_stats 自然降级不落）
IF OBJECT_ID(N'dbpilot_instance_disk') IS NULL
CREATE TABLE dbpilot_instance_disk (
    id                  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    instance_id         INT           NOT NULL,
    volume_mount_point  NVARCHAR(260) NOT NULL,
    total_mb            BIGINT        NULL,
    available_mb        BIGINT        NULL,
    used_pct            DECIMAL(5,2)  NULL,
    sample_time         DATETIME2(3)  NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_idisk')
    CREATE INDEX ix_idisk ON dbpilot_instance_disk (instance_id, sample_time);
GO
