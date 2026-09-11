/*
 * 索引碎片扫描冒烟脚本（IU-05）
 * 在被监控实例的冒烟库 dbpilot_smoke 执行（库由 smoke-index.sql 创建）。
 * 结构：① 建表造数（含制造碎片）→ ③ 自检 → ④ 清理（验证后取消注释执行）
 *
 * 场景模拟：
 *   1. GUID 聚集主键（随机插入 → 聚集索引天然高碎片）
 *   2. 非聚集索引 INCLUDE 大列（删除 40% + 键值随机重写 → 页分裂碎片）
 * 预期平台表现：
 *   索引使用页 → 碎片 tab → 选库 → 开始扫描（默认 256 页起）：
 *   - PK_SmokeFragOrders          碎片率高 → REBUILD
 *   - IX_SmokeFrag_CustomerId     碎片率中/高 → REORGANIZE 或 REBUILD
 *   两个索引页数均 > 1000，不会带“小表不建议”标注。
 */

USE dbpilot_smoke;
GO

--------------------------------------------------------------------------
-- ① 建表造数（建表 + 灌数 + 制造碎片）
--------------------------------------------------------------------------

-- 1. 建表 + 索引
IF OBJECT_ID('dbo.SmokeFragOrders') IS NOT NULL DROP TABLE dbo.SmokeFragOrders;
CREATE TABLE dbo.SmokeFragOrders (
    Id         uniqueidentifier NOT NULL CONSTRAINT df_smokefrag_id DEFAULT (NEWID()),
    OrderNo    bigint           NOT NULL IDENTITY(1, 1),
    CustomerId int              NOT NULL,
    Amount     decimal(12, 2)   NOT NULL,
    Payload    char(400)        NOT NULL CONSTRAINT df_smokefrag_payload DEFAULT ('x'),
    CONSTRAINT pk_smokefrag PRIMARY KEY CLUSTERED (Id)
);
CREATE NONCLUSTERED INDEX IX_SmokeFrag_CustomerId
    ON dbo.SmokeFragOrders (CustomerId) INCLUDE (Amount, Payload);
GO

-- 2. 灌 6 万行（约 3300 页，12 批 × 5000）
DECLARE @b int = 0;
WHILE @b < 12
BEGIN
    INSERT dbo.SmokeFragOrders (CustomerId, Amount)
    SELECT TOP (5000)
           ABS(CHECKSUM(NEWID())) % 10000,
           ABS(CHECKSUM(NEWID())) % 100000 / 100.0
    FROM sys.all_columns a CROSS JOIN sys.all_columns b;

    SET @b += 1;
END
GO

-- 3. 制造碎片：随机键重写（页分裂）+ 删除 40%（空页洞）
UPDATE dbo.SmokeFragOrders SET CustomerId = ABS(CHECKSUM(NEWID())) % 10000;
DELETE TOP (40) PERCENT FROM dbo.SmokeFragOrders;
GO

--------------------------------------------------------------------------
-- ③ 自检：DMV 应看到明显碎片（>30% 预期 REBUILD；10~30% REORGANIZE）
SELECT i.name,
       ips.index_type_desc,
       ips.partition_number,
       ips.avg_fragmentation_in_percent,   -- 碎片率
       ips.page_count                      -- 页数（需 >= 1000 才建议处理）
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.SmokeFragOrders'), NULL, NULL, 'LIMITED') ips
INNER JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
WHERE ips.index_id > 0;

--------------------------------------------------------------------------
-- ④ 清理（验证完后取消注释执行）
-- DROP TABLE dbo.SmokeFragOrders;
-- GO
