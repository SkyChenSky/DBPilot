-- smoke-deadlock-proc.sql : 存储过程内死锁（栈帧带 procname + 语句文本，
-- 与 adhoc 批的 "adhoc: unknown" 不同）。
-- 结构：① 建存储过程（直接执行；表来自 smoke-deadlock.sql，先跑一次它的建表段）
--        → ② 窗口脚本（两个窗口独立会话，窗口 2 在窗口 1 起跑约 2s 后执行）
--        → ③ 验证说明 → ④ 清理（验证后取消注释执行）

---------------------------------------------------------------------------
-- ① 建存储过程（幂等）
---------------------------------------------------------------------------
SET NOCOUNT ON;
USE dbpilot_smoke;
GO
IF OBJECT_ID(N'dbo.SmokeDlProc') IS NOT NULL DROP PROCEDURE dbo.SmokeDlProc;
GO
CREATE PROCEDURE dbo.SmokeDlProc @First CHAR(1)   -- 'A'：先锁 SmokeDlA 再锁 SmokeDlB；'B'：反序
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRAN;   -- 必须显式事务：自动提交逐语句放锁，不会互相等待
    IF @First = 'A'
    BEGIN
        UPDATE dbo.SmokeDlA SET V = V + 1 WHERE Id = 1;
        WAITFOR DELAY '00:00:10';
        UPDATE dbo.SmokeDlB SET V = V + 1 WHERE Id = 1;   -- 等窗口 2 → 死锁
    END
    ELSE
    BEGIN
        UPDATE dbo.SmokeDlB SET V = V + 1 WHERE Id = 1;
        WAITFOR DELAY '00:00:10';
        UPDATE dbo.SmokeDlA SET V = V + 1 WHERE Id = 1;   -- 等窗口 1 → 死锁
    END
    ROLLBACK;
END
GO

---------------------------------------------------------------------------
-- ② 窗口脚本
---------------------------------------------------------------------------

-- 窗口 1：先起跑
USE dbpilot_smoke;
EXEC dbo.SmokeDlProc 'A';
GO

-- 窗口 2（窗口 1 起跑约 2s 后执行）
USE dbpilot_smoke;
WAITFOR DELAY '00:00:02';
EXEC dbo.SmokeDlProc 'B';
GO

---------------------------------------------------------------------------
-- ③ 验证：DeadlockJob tick 后在 DBPilot 死锁页打开该事件详情，
--    执行栈帧应带 procname=dbo.SmokeDlProc 与语句文本（非 "adhoc: unknown"）
---------------------------------------------------------------------------

---------------------------------------------------------------------------
-- ④ 清理（验证后取消注释执行）
---------------------------------------------------------------------------
-- DROP PROCEDURE dbpilot_smoke.dbo.SmokeDlProc;
