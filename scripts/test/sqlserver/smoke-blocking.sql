-- smoke-blocking.sql : 阻塞分析页测试负载（BL-01）
-- 结构：① 建表造数（直接执行）→ ② 窗口脚本（每个窗口在独立 sqlcmd / SSMS
--        会话中执行该段；同一文件整跑会串行执行、产生不了阻塞）
--        → ③ 验证说明 → ④ 清理（验证后取消注释执行）
--
-- 制造两条阻塞链：
--   链 A（4 层，根 = 窗口 1）：W1 持 Id=1 行 X 锁睡眠；W2 持 Id=2 等 Id=1；
--                             W3 等 Id=2（X）；W4 等 Id=2（S，普通 SELECT）
--   链 B（2 层，根 = 窗口 5）：W5 持 Id=10 行 X 锁睡眠；W6 等 Id=10

---------------------------------------------------------------------------
-- ① 建表造数（幂等，任一会话跑一次）
---------------------------------------------------------------------------
SET NOCOUNT ON;
USE dbpilot_smoke;
GO
IF OBJECT_ID(N'dbo.SmokeBlockOrders') IS NULL
    CREATE TABLE dbo.SmokeBlockOrders (
        Id  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Amount DECIMAL(18,2) NOT NULL
    );
GO
IF NOT EXISTS (SELECT 1 FROM dbo.SmokeBlockOrders)
BEGIN
    INSERT INTO dbo.SmokeBlockOrders (Amount)
    VALUES (100.00), (100.00), (100.00), (100.00), (100.00),
           (100.00), (100.00), (100.00), (100.00), (100.00),
           (100.00), (100.00);
END
GO

---------------------------------------------------------------------------
-- ② 窗口脚本（每段在独立会话执行；按窗口编号依次起跑）
---------------------------------------------------------------------------

-- 窗口 1（链 A 根）：占住会话约 3 分钟后回滚
USE dbpilot_smoke;
BEGIN TRAN;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 1;
WAITFOR DELAY '00:03:00';
ROLLBACK;
GO

-- 窗口 2（链 A 第 1 层）：先拿 Id=2 的 X 锁再等 Id=1（两条 UPDATE 顺序不能颠倒）
USE dbpilot_smoke;
BEGIN TRAN;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 2;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 1;
ROLLBACK;
GO

-- 窗口 3（链 A 第 2 层）：被窗口 2 的 Id=2 X 锁阻塞
USE dbpilot_smoke;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 2;
GO

-- 窗口 4（链 A 第 2 层）：被窗口 2 的 Id=2 X 锁阻塞（S 锁）
USE dbpilot_smoke;
SELECT Amount FROM dbo.SmokeBlockOrders WHERE Id = 2;
GO

-- 窗口 5（链 B 根）：占住会话约 3 分钟后回滚
USE dbpilot_smoke;
BEGIN TRAN;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 10;
WAITFOR DELAY '00:03:00';
ROLLBACK;
GO

-- 窗口 6（链 B 第 1 层）：被窗口 5 的 Id=10 X 锁阻塞
USE dbpilot_smoke;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 10;
GO

---------------------------------------------------------------------------
-- ③ 验证：窗口全部等待期间打开 DBPilot 阻塞分析页（5s 轮询）
--    总览：2 条链、最长等待增长至约 3 分钟、涉及 6 个会话
--    树  ：链 A = 1 -> 2 -> {3, 4}；链 B = 5 -> 6
--    锁  ：根持有 dbo.SmokeBlockOrders 的 X 锁，受害者等 X / S
---------------------------------------------------------------------------

---------------------------------------------------------------------------
-- ④ 清理（所有窗口回滚 / 被 kill 之后，取消注释执行）
---------------------------------------------------------------------------
-- DROP TABLE dbpilot_smoke.dbo.SmokeBlockOrders;
