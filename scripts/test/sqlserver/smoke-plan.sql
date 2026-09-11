/*
 * 执行计划快照 / 计划变更冒烟脚本（PI-05）
 * 在被监控实例的冒烟库 dbpilot_smoke 执行（库由 smoke-index.sql 创建）。
 * 结构：① 建表造数 → ② 负载（阶段 1 → 等快照 tick → 阶段 2）
 *        → ③ 自检 → ④ 清理（验证后取消注释执行）
 *
 * RDS（SQL 2025）实测的坑：覆盖索引等值 COUNT 是 TRIVIAL 优化级别 →
 * 永远 Index Seek，计划永不变形。下面的查询加了非覆盖谓词（Pad），
 * 迫使优化器在 Seek+KeyLookup 与聚集 Scan 之间按成本二选一：
 *   阶段 1：@p='B'（1% 行） → 计划 1（Index Seek + Key Lookup）
 *   阶段 2：UPDATE STATISTICS FULLSCAN + sp_recompile + @p='A'（99%）
 *           → 计划 2（Clustered Index Scan，新 query_plan_hash）
 * 每阶段后等一个 QueryPlanJob tick（≤5 分钟），然后验证：
 *   curl /api/instances/{id}/top-sql/plan-changes  → 一条事件，avg 有差
 *   curl /api/instances/{id}/top-sql/plans?fingerprint=... → 两个版本
 *   plan-tree/{planId} → Index Seek / Clustered Index Scan 节点
 *   plan-xml/{planId}  → 非空 xml
 * 注：首个采集 tick 是种子（不产事件），属设计语义。
 */

--------------------------------------------------------------------------
-- ① 建表造数
--------------------------------------------------------------------------
USE dbpilot_smoke;
GO

IF OBJECT_ID('dbo.SmokePlan') IS NOT NULL DROP TABLE dbo.SmokePlan;
CREATE TABLE dbo.SmokePlan (
    Id  int       NOT NULL IDENTITY(1, 1) PRIMARY KEY,
    Col char(1)   NOT NULL,
    Pad char(200) NOT NULL CONSTRAINT df_smokeplan_pad DEFAULT ('x')
);
INSERT dbo.SmokePlan (Col)
SELECT TOP (10000) CASE WHEN ROW_NUMBER() OVER (ORDER BY a.column_id) <= 100 THEN 'B' ELSE 'A' END
FROM sys.all_columns a CROSS JOIN sys.all_columns b;
CREATE INDEX IX_SmokePlan_Col ON dbo.SmokePlan (Col);
GO

--------------------------------------------------------------------------
-- ② 负载
--------------------------------------------------------------------------

-- 阶段 1：稀有值 'B' → Index Seek + Key Lookup
DECLARE @i int = 0, @c int;
WHILE @i < 10
BEGIN
    EXEC sp_executesql N'SELECT @c = COUNT(*) FROM dbo.SmokePlan WHERE Col = @p AND Pad = @d',
         N'@p char(1), @d char(1), @c int OUTPUT', @p = 'B', @d = 'x', @c = @c OUTPUT;
    SET @i += 1;
END
GO

-- ③ 自检：当前计划形状（预期 Index Seek + Key Lookup）
SELECT qs.query_plan_hash, qs.execution_count,
       CONVERT(nvarchar(max), qp.query_plan) AS plan_xml
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE N'%SmokePlan%' AND st.text NOT LIKE '%sp_executesql%'
ORDER BY qs.creation_time DESC;
GO

-- >>> 在此等一个计划快照 tick（≤5 分钟），再跑下面的阶段 2 <<<

-- 阶段 2：刷新统计 + 失效计划 + 常见值 'A' → 新计划（Scan）
UPDATE STATISTICS dbo.SmokePlan WITH FULLSCAN;
EXEC sp_recompile 'dbo.SmokePlan';
GO
DECLARE @j int = 0, @c2 int;
WHILE @j < 10
BEGIN
    EXEC sp_executesql N'SELECT @c2 = COUNT(*) FROM dbo.SmokePlan WHERE Col = @p AND Pad = @d',
         N'@p char(1), @d char(1), @c2 int OUTPUT', @p = 'A', @d = 'x', @c2 = @c2 OUTPUT;
    SET @j += 1;
END
GO

-- ③ 自检：应出现第二个 query_plan_hash（Clustered Index Scan）
SELECT qs.query_plan_hash, qs.creation_time, qs.execution_count,
       qs.total_logical_reads / qs.execution_count AS avg_reads,
       CONVERT(nvarchar(max), qp.query_plan) AS plan_xml
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE N'%SmokePlan%' AND st.text NOT LIKE '%sp_executesql%'
ORDER BY qs.creation_time DESC;
GO

--------------------------------------------------------------------------
-- ④ 清理（验证完后取消注释执行）
-- DROP TABLE dbo.SmokePlan;
-- GO
