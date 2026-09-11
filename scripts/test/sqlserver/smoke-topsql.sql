/*
 * 实时 Top SQL 冒烟脚本（TS-01）
 * 在被监控实例的冒烟库 dbpilot_smoke 执行（库由 smoke-index.sql 创建）。
 * 结构：① 建表造数 → ② 负载 → ③ 自检 → ④ 清理（验证后取消注释执行）
 *
 * 场景模拟（三种负载特征，验证不同排序口径）：
 *   1. 高频短查询（同一语句不同参数 × 2000）→ 总耗时榜前列 + 验证指纹合并（次数 2000+）
 *      平台指纹口径 = query_hash 优先，query_hash 为 NULL 时兜底 sql_handle（旧式 adhoc 语句）
 *   2. 低频慢查询（WAITFOR 500ms × 20）     → 平均耗时榜第一（CPU 却很低）
 *   3. 重读查询（大范围扫描 × 50）          → 逻辑读榜前列
 * 预期平台表现（性能优化 → Top SQL）：
 *   - 按“总耗时”：1) 与 3) 在前，2) 靠后
 *   - 按“平均耗时”：2)（≈500ms）第一
 *   - 1) 执行次数 ≈ 2000；同一语句的多参数执行只占一行（指纹合并生效）
 *   - WAITFOR 语句 CPU ≈ 0（等待型，非计算型）
 */

USE dbpilot_smoke;
GO

---------------------------------------------------------------------------
-- ① 建表造数（自包含数据表，1 万行）
---------------------------------------------------------------------------
IF OBJECT_ID('dbo.SmokeTopSql') IS NOT NULL DROP TABLE dbo.SmokeTopSql;
CREATE TABLE dbo.SmokeTopSql (
    Id   int           NOT NULL IDENTITY(1, 1) PRIMARY KEY,
    Cat  int           NOT NULL,
    Val  decimal(12,2) NOT NULL,
    Data char(200)     NOT NULL CONSTRAINT df_smoketopsql_data DEFAULT ('x')
);
INSERT dbo.SmokeTopSql (Cat, Val)
SELECT TOP (10000) ABS(CHECKSUM(NEWID())) % 100, ABS(CHECKSUM(NEWID())) % 100000 / 100.0
FROM sys.all_columns a CROSS JOIN sys.all_columns b;
GO

--------------------------------------------------------------------------
-- ② 负载（三种特征）
--------------------------------------------------------------------------

-- 1) 高频短查询：同一语句、参数变化（2000 次 → 指纹合并后 execution_count ≈ 2000）
DECLARE @i int = 0, @c int;
WHILE @i < 2000
BEGIN
    SELECT @c = COUNT(*) FROM dbo.SmokeTopSql WHERE Cat = @i % 100;   -- 参数化：语句文本一致
    SET @i += 1;
END
GO

-- 2) 低频慢查询（等待型）：平均耗时高、CPU 低
DECLARE @j int = 0;
WHILE @j < 20
BEGIN
    WAITFOR DELAY '00:00:00.500';
    SET @j += 1;
END
GO

-- 3) 重读查询：大范围扫描（逻辑读高）
DECLARE @k int = 0, @s decimal(18, 2);
WHILE @k < 50
BEGIN
    SELECT @s = SUM(Val) FROM dbo.SmokeTopSql WHERE Id > 100;
    SET @k += 1;
END
GO

--------------------------------------------------------------------------
-- ③ 自检：应看到三类语句；平台按指纹合并为一行（query_hash 优先、sql_handle 兜底）
SELECT TOP 20
       qs.query_hash,
       qs.execution_count,
       qs.total_elapsed_time / 1000 AS total_ms,
       qs.total_elapsed_time / 1000 / qs.execution_count AS avg_ms,
       qs.total_worker_time / 1000 AS cpu_ms,
       qs.total_logical_reads,
       SUBSTRING(st.text, (qs.statement_start_offset / 2) + 1,
           ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                                          ELSE qs.statement_end_offset END
             - qs.statement_start_offset) / 2) + 1) AS statement_text
FROM sys.dm_exec_query_stats qs
OUTER APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.dbid = DB_ID()
ORDER BY qs.total_elapsed_time DESC;

--------------------------------------------------------------------------
-- ④ 清理（验证完后取消注释执行）
-- DROP TABLE dbo.SmokeTopSql;
-- GO

-- 注：语句级统计在计划缓存被挤出后会丢失；若平台页看不到预期语句，重跑本脚本即可。
