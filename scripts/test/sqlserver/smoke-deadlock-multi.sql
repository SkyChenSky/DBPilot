-- smoke-deadlock-multi.sql : 三方（N 方）死锁 —— 3 个会话构成环形等待。
-- 结构：① 无自建表（复用 smoke-deadlock.sql 的 SmokeDlA/B、SmokeRlC，先跑一次它的建表段）
--        → ② 窗口脚本（三个窗口独立会话、间隔约 2s 依次起跑、各自整批执行）
--        → ③ 验证说明 → ④ 无需清理
--
-- 死锁即等待环：N 个会话各持一把资源、依次申请下一把，构成 N 方环。
-- SQL Server 监视器处理多进程环（xml_deadlock_report 会列出 N 个 <process>，
-- 通常 1 个受害者）。环（刻意混合锁类型 —— 一次事件、两种趋势色）：
--   窗口 1：持 SmokeDlA（KEY） → 申请 SmokeDlB（KEY）
--   窗口 2：持 SmokeDlB（KEY） → 申请 SmokeRlC（RID，堆）
--   窗口 3：持 SmokeRlC（RID） → 申请 SmokeDlA（KEY）

---------------------------------------------------------------------------
-- ② 窗口脚本（各持首锁 15s，可容忍约 10s 起跑偏差；环在约 15~19s 时闭合，
--    监视器再过约 5s 杀一个受害者，其余两个会话串行跑完。总耗时约 40s）
---------------------------------------------------------------------------

-- 窗口 1（t0 起跑）
USE dbpilot_smoke;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeDlA SET V = V + 1 WHERE Id = 1;   -- 持 A（KEY）
    WAITFOR DELAY '00:00:15';
    UPDATE dbo.SmokeDlB SET V = V + 1 WHERE Id = 1;   -- 等窗口 2
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '3way w1: ' + ERROR_MESSAGE();
END CATCH
GO

-- 窗口 2（t0 + 约 2s）
USE dbpilot_smoke;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeDlB SET V = V + 1 WHERE Id = 1;   -- 持 B（KEY）
    WAITFOR DELAY '00:00:15';
    UPDATE dbo.SmokeRlC SET V = V + 1 WHERE Id = 1;   -- 等窗口 3
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '3way w2: ' + ERROR_MESSAGE();
END CATCH
GO

-- 窗口 3（t0 + 约 4s）
USE dbpilot_smoke;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRAN;
    UPDATE dbo.SmokeRlC SET V = V + 1 WHERE Id = 1;   -- 持 C（RID）
    WAITFOR DELAY '00:00:15';
    UPDATE dbo.SmokeDlA SET V = V + 1 WHERE Id = 1;   -- 等窗口 1 → 成环
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT '3way w3: ' + ERROR_MESSAGE();
END CATCH
GO

---------------------------------------------------------------------------
-- ③ 验证：DeadlockJob tick 后
--    列表 processCount = 3；趋势 keyLocks 与 ridLocks 各 +1
---------------------------------------------------------------------------
