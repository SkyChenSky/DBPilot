/*
================================================================================
 DBPilot 索引诊断冒烟脚本（步骤 4：索引缺失 MI / 索引使用率 IU）
================================================================================
结构    ：① 建库建表灌数 → ② 负载（触发 DMV 记录）→ ③ 自检 → ④ 清理
用途    ：在被监控的 SQL Server 实例上制造两个真实场景，验证 DBPilot 两页诊断结果
前提    ：登录账号需有建库权限（如 sky / dba）；无需其他准备
用法    ：SSMS 连到被监控实例，整段执行（约 30~60 秒），
          然后打开 DBPilot → 性能优化 → 索引缺失 / 索引使用率，
          选该实例 + 库 [dbpilot_smoke] 进行诊断
场景一  ：缺失索引 —— SmokeOrders(5 万行) 上按无索引列（CustomerId/Status/Amount）
          循环查询。RDS(SQL 2025) 实测仅聚合形态触发（普通等值+范围 SELECT 优化器
          不认为值得建索引，估计计划无 MissingIndexes 元素）：
            ① 等值+不等值+包含列（CustomerId = ? AND Amount > ?）→ 实测不触发，保留仅为负载
            ② 聚合等值（MAX(Amount) WHERE Status = ?）→ 触发：eq=[Status] inc=[Amount]
场景一b ：多表多条缺失索引（3 张新表 × 不同形态，全部实测触发，共 5 条）：
            SmokeUsers    eq=[RegionCode] / eq=[RegionCode, Tier]
            SmokeProducts eq=[CategoryId] inc=[Price]
            SmokeEvents   eq=[SessionId] / eq=[SessionId] ineq=[Value]
          注意：聚合+等值列选择性太差（如 Tier 仅 4 个值）不触发；
          全脚本合计 6 条建议（含场景一的 [Status]）
场景二  ：未使用索引 —— SmokeLogs(3 万行) 上建 IX_SmokeLogs_CorrelationId，
          只写不读：UPDATE 1 万+ 次（读恒为 0、写放大明显、索引页数 >1000）
          → IU-02 应将其标记“未使用”并给 DISABLE 脚本
注意    ：缺失索引 DMV 为内存态，实例重启后清空 —— 这正好验证页面
          “实例运行不足 30 天数据可能不完整”警示逻辑
================================================================================
*/

---------------------------------------------------------------------------
-- ① 建库建表灌数（幂等）
---------------------------------------------------------------------------
IF DB_ID(N'dbpilot_smoke') IS NULL
    CREATE DATABASE dbpilot_smoke;
GO
USE dbpilot_smoke;
GO

-- 场景一：缺失索引（SmokeOrders，5 万行）
IF OBJECT_ID(N'dbo.SmokeOrders') IS NULL
BEGIN
    CREATE TABLE dbo.SmokeOrders
    (
        Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_smokeorders PRIMARY KEY,
        CustomerId INT              NOT NULL,   -- 无索引（等值候选）
        Status     TINYINT          NOT NULL,   -- 无索引（等值候选）
        Amount     DECIMAL(18,2)    NOT NULL,   -- 无索引（不等值候选）
        Remark     NVARCHAR(200)    NOT NULL DEFAULT N'备注占位'  -- 包含列候选
    );

    -- 5 万行测试数据
    ;WITH n AS (SELECT TOP (50000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
    INSERT INTO dbo.SmokeOrders (CustomerId, Status, Amount, Remark)
    SELECT rn % 1000, rn % 5, (rn % 900) + 0.5, N'订单' + CAST(rn AS NVARCHAR(20))
    FROM n;
END
GO

-- 场景一b：多表多条缺失索引
IF OBJECT_ID(N'dbo.SmokeUsers') IS NULL
BEGIN
    CREATE TABLE dbo.SmokeUsers
    (
        Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_smokeusers PRIMARY KEY,
        RegionCode INT           NOT NULL,   -- 无索引（等值候选，500 个值）
        Tier       TINYINT       NOT NULL,   -- 无索引（第二等值列；单用不触发——仅 4 个值选择性太差）
        Points     INT           NOT NULL,
        Memo       NVARCHAR(100) NOT NULL DEFAULT N'备注'
    );

    ;WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
    INSERT INTO dbo.SmokeUsers (RegionCode, Tier, Points)
    SELECT rn % 500, rn % 4, (rn % 9000) + 1
    FROM n;
END
GO

IF OBJECT_ID(N'dbo.SmokeProducts') IS NULL
BEGIN
    CREATE TABLE dbo.SmokeProducts
    (
        Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_smokeproducts PRIMARY KEY,
        CategoryId INT           NOT NULL,   -- 无索引（等值候选，100 个值）
        Price      DECIMAL(18,2) NOT NULL,   -- MAX 聚合 → 进包含列
        Stock      INT           NOT NULL
    );

    ;WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
    INSERT INTO dbo.SmokeProducts (CategoryId, Price, Stock)
    SELECT rn % 100, (rn % 50000) / 100.0, rn % 500
    FROM n;
END
GO

IF OBJECT_ID(N'dbo.SmokeEvents') IS NULL
BEGIN
    CREATE TABLE dbo.SmokeEvents
    (
        Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_smokeevents PRIMARY KEY,
        SessionId INT           NOT NULL,    -- 无索引（等值候选，10000 个值）
        EventTime DATETIME2(3)  NOT NULL,    -- MAX 聚合 → 进包含列
        Value     INT           NOT NULL     -- 不等值候选
    );

    ;WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
    INSERT INTO dbo.SmokeEvents (SessionId, EventTime, Value)
    SELECT rn % 10000, DATEADD(SECOND, rn, '2026-01-01'), rn % 300
    FROM n;
END
GO

-- 场景二：未使用索引（SmokeLogs，3 万行；宽键 3 万行 × ~400B ≈ 1200+ 页）
IF OBJECT_ID(N'dbo.SmokeLogs') IS NULL
BEGIN
    CREATE TABLE dbo.SmokeLogs
    (
        Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_smokelogs PRIMARY KEY,
        CorrelationId  CHAR(400)      NOT NULL DEFAULT 'x',
        Payload        NVARCHAR(100)  NOT NULL DEFAULT N'负载'
    );

    ;WITH n AS (SELECT TOP (30000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
    INSERT INTO dbo.SmokeLogs (CorrelationId)
    SELECT REPLICATE('a', 390) + RIGHT('00000' + CAST(rn AS VARCHAR(5)), 5)
    FROM n;

    -- 从不查询的索引（读恒为 0）
    CREATE INDEX IX_SmokeLogs_CorrelationId ON dbo.SmokeLogs (CorrelationId);
END
GO

---------------------------------------------------------------------------
-- ② 负载：触发 DMV 记录
---------------------------------------------------------------------------

-- 场景一：200 轮 × 2 类查询（循环参数让优化器无法参数嗅探固定值）
DECLARE @i INT = 0, @cid INT, @st TINYINT, @amt DECIMAL(18,2), @r NVARCHAR(200);
WHILE @i < 200
BEGIN
    SET @cid = @i % 1000;
    SET @st  = @i % 5;

    -- ① 等值 + 不等值 + 包含列：CustomerId = ? AND Amount > ?，SELECT Remark
    --    （RDS 实测不触发建议，保留作为负载；且若已建 CustomerId 索引会走索引）
    SELECT @r = Remark
    FROM dbo.SmokeOrders
    WHERE CustomerId = @cid AND Amount > 500;

    -- ② 聚合等值：MAX(Amount) WHERE Status = ? → eq=[Status] inc=[Amount]
    SELECT @amt = MAX(Amount)
    FROM dbo.SmokeOrders
    WHERE Status = @st;

    SET @i += 1;
END
GO

-- 场景一b：触发 5 条建议（形态均为实测稳定触发器：聚合 MAX + 等值/多列等值/等值+不等值）
DECLARE @i INT = 0, @rc INT, @tier TINYINT, @cat INT, @sid INT;
DECLARE @v INT; DECLARE @t DATETIME2(3); DECLARE @p DECIMAL(18,2);
WHILE @i < 200
BEGIN
    SET @rc = @i % 500; SET @tier = @i % 4; SET @cat = @i % 100; SET @sid = @i % 10000;

    -- 等值 → eq=[RegionCode]
    SELECT @v = MAX(Points)
    FROM dbo.SmokeUsers
    WHERE RegionCode = @rc;

    -- 多列等值 → eq=[RegionCode, Tier]
    SELECT @v = MAX(Points)
    FROM dbo.SmokeUsers
    WHERE RegionCode = @rc AND Tier = @tier;

    -- 等值 + 包含列 → eq=[CategoryId] inc=[Price]
    SELECT @p = MAX(Price)
    FROM dbo.SmokeProducts
    WHERE CategoryId = @cat;

    -- 等值 → eq=[SessionId]
    SELECT @v = MAX(Value)
    FROM dbo.SmokeEvents
    WHERE SessionId = @sid;

    -- 等值 + 不等值 → eq=[SessionId] ineq=[Value]
    SELECT @t = MAX(EventTime)
    FROM dbo.SmokeEvents
    WHERE SessionId = @sid AND Value > 200;

    SET @i += 1;
END
GO

-- 场景二：只写不读，UPDATE 索引键本身 1 万零 50 次（IU-02 要求 user_updates > 10000）
-- 注意必须改索引键：改非键列不会让未读索引进入 dm_db_index_usage_stats（无行 = 查询会漏）
DECLARE @i INT = 0, @id INT;
WHILE @i < 10050
BEGIN
    SET @id = (@i % 30000) + 1;
    UPDATE dbo.SmokeLogs
    SET CorrelationId = LEFT(CorrelationId, 390) + RIGHT('00000' + CAST(@i % 100000 AS VARCHAR(5)), 5)
    WHERE Id = @id;
    SET @i += 1;
END
GO

---------------------------------------------------------------------------
-- ③ 自检：直接在库内看 DMV，与 DBPilot 页面结果互相印证
---------------------------------------------------------------------------

-- 缺失索引建议（全脚本预期 6 条：SmokeOrders [Status] +
--   SmokeUsers [RegionCode] / [RegionCode,Tier] +
--   SmokeProducts [CategoryId]+inc[Price] +
--   SmokeEvents [SessionId] / [SessionId]+ineq[Value]）
SELECT mid.statement, mid.equality_columns, mid.inequality_columns, mid.included_columns,
       migs.user_seeks, migs.user_seeks * migs.avg_total_user_cost * migs.avg_user_impact * 0.01 AS score
FROM sys.dm_db_missing_index_group_stats migs
JOIN sys.dm_db_missing_index_groups mig ON mig.index_group_handle = migs.group_handle
JOIN sys.dm_db_missing_index_details mid ON mid.index_handle = mig.index_handle
WHERE mid.database_id = DB_ID()
ORDER BY score DESC;

-- 索引使用率（与 Provider 同构：sys.indexes 出发 LEFT JOIN；
-- IX_SmokeLogs_CorrelationId 应为 读=0 / updates>10000 / 页数>1000）
SELECT i.name, ISNULL(u.user_seeks, 0) AS seeks, ISNULL(u.user_scans, 0) AS scans,
       ISNULL(u.user_lookups, 0) AS lookups, ISNULL(u.user_updates, 0) AS updates, ps.used_page_count
FROM sys.indexes i
INNER JOIN sys.objects o ON o.object_id = i.object_id
LEFT JOIN sys.dm_db_index_usage_stats u
       ON u.object_id = i.object_id AND u.index_id = i.index_id AND u.database_id = DB_ID()
OUTER APPLY (SELECT SUM(p.used_page_count) AS used_page_count
             FROM sys.dm_db_partition_stats p
             WHERE p.object_id = i.object_id AND p.index_id = i.index_id) ps
WHERE o.is_ms_shipped = 0 AND i.index_id > 0 AND i.name IS NOT NULL AND i.is_disabled = 0
  AND i.name = 'IX_SmokeLogs_CorrelationId';
GO

---------------------------------------------------------------------------
-- ④ 清理（测试完成后取消注释执行：删除测试库）
---------------------------------------------------------------------------
/*
USE master;
ALTER DATABASE dbpilot_smoke SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE dbpilot_smoke;
*/
