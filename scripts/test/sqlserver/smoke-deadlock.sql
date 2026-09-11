-- smoke-deadlock.sql : 死锁冒烟负载（DL-01 采集 → DL-02 列表 → DL-05 趋势）
-- 结构：① 建表造数（直接执行）→ ② 窗口脚本（两个窗口在独立会话中、
--        各自整批一次执行；窗口 2 在窗口 1 起跑约 2s 后执行）
--        → ③ 验证说明 → ④ 清理（验证后取消注释执行）
--
-- 每种锁类型制造一个死锁，让趋势图出现全部四色（KEY / RID / PAG / OBJ）。
-- 窗口整批跑四个场景，外层 TRY/CATCH：死锁 1205 只中断当前场景（引擎已回滚
-- 该事务），批继续跑下一个场景：
--   场景 1  keylock    UPDATE 聚集 PK 表的行   (SmokeDlA/B)
--   场景 2  ridlock    UPDATE 堆表（无索引）的行 (SmokeRlC/D)
--   场景 3  pagelock   SELECT ... WITH (PAGLOCK, XLOCK)
--   场景 4  objectlock SELECT ... WITH (TABLOCKX)
-- 未覆盖（无法确定性复现）：exchangeEvent（需并行计划阻塞交换）、
-- metadatalock（需 DDL 时序）。

---------------------------------------------------------------------------
-- ① 建表造数（幂等，任一会话跑一次）
---------------------------------------------------------------------------
SET NOCOUNT ON;
USE dbpilot_smoke;
GO
-- keylock 表：聚集 PK
IF OBJECT_ID(N'dbo.SmokeDlA') IS NULL
    CREATE TABLE dbo.SmokeDlA (Id INT NOT NULL PRIMARY KEY, V INT NOT NULL);
IF OBJECT_ID(N'dbo.SmokeDlB') IS NULL
    CREATE TABLE dbo.SmokeDlB (Id INT NOT NULL PRIMARY KEY, V INT NOT NULL);
GO
-- ridlock/pagelock 表：堆表（刻意无索引，行锁是 RID 且整表在一页上）
IF OBJECT_ID(N'dbo.SmokeRlC') IS NULL
    CREATE TABLE dbo.SmokeRlC (Id INT NOT NULL, V INT NOT NULL);
IF OBJECT_ID(N'dbo.SmokeRlD') IS NULL
    CREATE TABLE dbo.SmokeRlD (Id INT NOT NULL, V INT NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM dbo.SmokeDlA) INSERT INTO dbo.SmokeDlA VALUES (1, 0);
IF NOT EXISTS (SELECT 1 FROM dbo.SmokeDlB) INSERT INTO dbo.SmokeDlB VALUES (1, 0);
IF NOT EXISTS (SELECT 1 FROM dbo.SmokeRlC) INSERT INTO dbo.SmokeRlC VALUES (1, 0);
IF NOT EXISTS (SELECT 1 FROM dbo.SmokeRlD) INSERT INTO dbo.SmokeRlD VALUES (1, 0);
GO

---------------------------------------------------------------------------
-- ② 窗口脚本（独立会话，整批一次执行；两窗口都先持首锁 10s 再申请第二把，
--    只要窗口 2 在窗口 1 之后 10s 内起跑，死锁环必然闭合。
--    总耗时约 85s（4 场景 × 约 21s 含受害者检测））
---------------------------------------------------------------------------

-- 窗口 1：先起跑
USE dbpilot_smoke;
SET NOCOUNT ON;

-- 1) keylock：聚集 PK 行 X 锁
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeDlA SET V = V + 1 WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    UPDATE dbo.SmokeDlB SET V = V + 1 WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '1 keylock: ' + ERROR_MESSAGE();
END CATCH

-- 2) ridlock：堆行 X 锁（无索引 → RID）
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeRlC SET V = V + 1 WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    UPDATE dbo.SmokeRlD SET V = V + 1 WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '2 ridlock: ' + ERROR_MESSAGE();
END CATCH

-- 3) pagelock：用提示取页 X 锁（单页表；别加 HOLDLOCK —— 它会在对象级持
--    S 锁，死锁环在对象层闭合变成 objectlock）
BEGIN TRY
    BEGIN TRAN;
    SELECT V FROM dbo.SmokeRlC WITH (PAGLOCK, XLOCK) WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    SELECT V FROM dbo.SmokeRlD WITH (PAGLOCK, XLOCK) WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '3 pagelock: ' + ERROR_MESSAGE();
END CATCH

-- 4) objectlock：TABLOCKX 取表 X 锁
BEGIN TRY
    BEGIN TRAN;
    SELECT V FROM dbo.SmokeDlA WITH (TABLOCKX) WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    SELECT V FROM dbo.SmokeDlB WITH (TABLOCKX) WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '4 objectlock: ' + ERROR_MESSAGE();
END CATCH
GO

-- 窗口 2（窗口 1 起跑约 2s 后执行；同场景、表序颠倒）
USE dbpilot_smoke;
SET NOCOUNT ON;

-- 1) keylock（表序颠倒）
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeDlB SET V = V + 1 WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    UPDATE dbo.SmokeDlA SET V = V + 1 WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '1 keylock: ' + ERROR_MESSAGE();
END CATCH

-- 2) ridlock（表序颠倒）
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeRlD SET V = V + 1 WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    UPDATE dbo.SmokeRlC SET V = V + 1 WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '2 ridlock: ' + ERROR_MESSAGE();
END CATCH

-- 3) pagelock（表序颠倒）
BEGIN TRY
    BEGIN TRAN;
    SELECT V FROM dbo.SmokeRlD WITH (PAGLOCK, XLOCK) WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    SELECT V FROM dbo.SmokeRlC WITH (PAGLOCK, XLOCK) WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '3 pagelock: ' + ERROR_MESSAGE();
END CATCH

-- 4) objectlock（表序颠倒）
BEGIN TRY
    BEGIN TRAN;
    SELECT V FROM dbo.SmokeDlB WITH (TABLOCKX) WHERE Id = 1;
    WAITFOR DELAY '00:00:10';
    SELECT V FROM dbo.SmokeDlA WITH (TABLOCKX) WHERE Id = 1;
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '4 objectlock: ' + ERROR_MESSAGE();
END CATCH
GO

---------------------------------------------------------------------------
-- ③ 验证：等 DeadlockJob tick（最长 60s）后
--    GET /api/instances/{id}/deadlocks/stats -> keyLocks/ridLocks/pageLocks/objectLocks 各 +1
--    DBPilot 死锁页趋势图出现四色
---------------------------------------------------------------------------

---------------------------------------------------------------------------
-- ④ 清理（验证后取消注释执行）
---------------------------------------------------------------------------
-- DROP TABLE dbpilot_smoke.dbo.SmokeDlA;
-- DROP TABLE dbpilot_smoke.dbo.SmokeDlB;
-- DROP TABLE dbpilot_smoke.dbo.SmokeRlC;
-- DROP TABLE dbpilot_smoke.dbo.SmokeRlD;
