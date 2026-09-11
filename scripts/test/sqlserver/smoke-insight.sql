-- smoke-insight.sql : 性能洞察页测试负载（PI-01/02 AAS + 等待桶）
-- 结构：① 建表造数（直接执行，约 1 分钟，构建约 600 MB 的 SmokeIoRows）
--        → ② 窗口脚本（每个窗口在独立 sqlcmd / SSMS 会话中执行该段；
--           同一文件整跑会串行执行、混不出等待桶分层）
--        → ③ 验证说明 → ④ 清理（验证后取消注释执行）
--
-- 制造约 2 分钟的混合等待桶负载，让 AAS 堆叠面积图出现真实分层。
-- 普通实例可覆盖的桶：
--   桶            窗口      等待类型              说明
--   ----------   -------  -------------------  --------------------------------
--   cpu          1        （运行中）             紧循环，无等待类型
--   lock         2,3      LCK_M_*               与 smoke-blocking 同款链
--   userWait     4        WAITFOR               用户等待，计为活跃
--   logWrite     5        WRITELOG              每次提交循环插一行
--   userIo       6        PAGEIOLATCH_SH        大表扫描（约 600 MB）
--   parallel     7        CXPACKET/CXCONSUMER   大交叉连接走并行
--   bufferLatch  8 (×2)   PAGELATCH_EX          末页插入热点
--   network      9        ASYNC_NETWORK_IO      大结果集发给慢客户端
--   memory       10 (×2)  RESOURCE_SEMAPHORE    并发大排序（可能需要较小
--                                                 内存上限才会排队）
--   latch/其他   11       LATCH_/PREEMPTIVE_*   DBCC CHECKTABLE（混合等待）
-- 脚本无法覆盖（依赖环境，列全备查）：threads（需耗尽 worker）、
-- backup（RDS 禁 BACKUP TO DISK）、hadr / trace / broker / fullText / sysIo。

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
IF OBJECT_ID(N'dbo.SmokePerfRows') IS NULL
    CREATE TABLE dbo.SmokePerfRows (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Pad CHAR(200) NOT NULL
    );
GO
IF NOT EXISTS (SELECT 1 FROM dbo.SmokePerfRows)
BEGIN
    INSERT INTO dbo.SmokePerfRows (Pad)
    SELECT TOP (2000) 'x' FROM sys.all_columns a CROSS JOIN sys.all_columns b;
END
GO
-- userIo / parallel / memory 窗口用的大表（约 600 MB）
IF OBJECT_ID(N'dbo.SmokeIoRows') IS NULL
    CREATE TABLE dbo.SmokeIoRows (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Pad CHAR(4000) NOT NULL DEFAULT 'x'
    );
GO
IF NOT EXISTS (SELECT 1 FROM dbo.SmokeIoRows)
BEGIN
    INSERT INTO dbo.SmokeIoRows (Pad)
    SELECT TOP (150000) 'x' FROM sys.all_columns a CROSS JOIN sys.all_columns b;
END
GO
-- logWrite 窗口的目标表（小行，每次插入一次日志刷盘）
IF OBJECT_ID(N'dbo.SmokeLogRows') IS NULL
    CREATE TABLE dbo.SmokeLogRows (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Pad CHAR(10) NOT NULL DEFAULT 'y'
    );
GO
-- bufferLatch 窗口的末页热点表（自增顺序键）
IF OBJECT_ID(N'dbo.SmokeHotRows') IS NULL
    CREATE TABLE dbo.SmokeHotRows (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Pad CHAR(10) NOT NULL DEFAULT 'z'
    );
GO

---------------------------------------------------------------------------
-- ② 窗口脚本（独立会话执行；窗口 8 / 10 各需两个会话同时跑）
---------------------------------------------------------------------------

-- 窗口 1（cpu，约 2 分钟）：紧循环
USE dbpilot_smoke;
DECLARE @i INT = 0, @n INT;
WHILE @i < 4000
BEGIN
    SELECT @n = COUNT(*) FROM dbo.SmokePerfRows a CROSS JOIN dbo.SmokePerfRows b
    WHERE a.Id % 100 = 1 AND b.Id % 997 = 1;
    SET @i += 1;
END;
GO

-- 窗口 2（lock 根；与 smoke-blocking 的链 B 根相同）
USE dbpilot_smoke;
BEGIN TRAN;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 10;
WAITFOR DELAY '00:02:00';
ROLLBACK;
GO

-- 窗口 3（lock 受害者；被窗口 2 阻塞）
USE dbpilot_smoke;
UPDATE dbo.SmokeBlockOrders SET Amount = Amount + 1 WHERE Id = 10;
GO

-- 窗口 4（userWait）：WAITFOR 计为活跃，落入 userWait 桶
USE dbpilot_smoke;
WAITFOR DELAY '00:02:00';
GO

-- 窗口 5（logWrite）：紧的单行插入循环，每次提交都刷日志（WRITELOG）。
-- 简单恢复模式下日志会复用，但等待仍然出现
USE dbpilot_smoke;
DECLARE @i INT = 0;
WHILE @i < 200000
BEGIN
    INSERT INTO dbo.SmokeLogRows DEFAULT VALUES;
    SET @i += 1;
END;
GO

-- 窗口 6（userIo）：约 600 MB 大表全扫。第一遍是 IO 瓶颈（PAGEIOLATCH_SH）；
-- 若实例缓冲池够大，表被缓存后后续几遍变成 cpu。要再次强制物理 IO，可在
-- 两遍之间用特权会话跑（仅 RDS admin 可用）：CHECKPOINT; DBCC DROPCLEANBUFFERS;
USE dbpilot_smoke;
DECLARE @i INT = 0, @n BIGINT;
WHILE @i < 20
BEGIN
    SELECT @n = SUM(CAST(LEN(Pad) AS BIGINT)) FROM dbo.SmokeIoRows;
    SET @i += 1;
END;
GO

-- 窗口 7（parallel）：大表交叉连接，默认 MAXDOP > 1 时计划走并行（CXPACKET / CXCONSUMER）
USE dbpilot_smoke;
DECLARE @i INT = 0, @n BIGINT;
WHILE @i < 5
BEGIN
    SELECT @n = COUNT(*) FROM dbo.SmokeIoRows a CROSS JOIN dbo.SmokeIoRows b
    WHERE a.Id % 1000 = 1 AND b.Id % 991 = 1;
    SET @i += 1;
END;
GO

-- 窗口 8（bufferLatch，两个会话同时跑）：自增顺序键的并发插入打同一个末页
--（PAGELATCH_EX 热点，典型 tempdb 式症状）
USE dbpilot_smoke;
DECLARE @i INT = 0;
WHILE @i < 100000
BEGIN
    INSERT INTO dbo.SmokeHotRows DEFAULT VALUES;
    SET @i += 1;
END;
GO

-- 窗口 9（network）：400 万行结果集发给慢客户端。引擎等客户端取行
--（ASYNC_NETWORK_IO）。客户端 Ctrl+C 结束
USE dbpilot_smoke;
SELECT a.Id AS IdA, b.Id AS IdB
FROM dbo.SmokePerfRows a CROSS JOIN dbo.SmokePerfRows b;
GO

-- 窗口 10（memory，两个会话同时跑）：两个大并发排序内存授予。达到内存授予
-- 上限时通常一个排队 RESOURCE_SEMAPHORE；大内存实例可能两个并行跑完、该桶
-- 为空（依赖环境）
USE dbpilot_smoke;
DECLARE @i INT = 0;
WHILE @i < 3
BEGIN
    SELECT Id, Pad FROM dbo.SmokeIoRows ORDER BY Pad DESC;
    SET @i += 1;
END;
GO

-- 窗口 11（latch / preemptive 混合，可选）：DBCC CHECKTABLE 校验大表时
-- 产生零星 LATCH_* 与 PREEMPTIVE_* 等待
USE dbpilot_smoke;
DBCC CHECKTABLE(N'SmokeIoRows') WITH NO_INFOMSGS;
GO

---------------------------------------------------------------------------
-- ③ 验证：窗口运行期间打开 DBPilot 性能洞察页（实时最近 5 分钟、10s 轮询、
--    等待维度），AAS 堆叠分层，每桶一层，样本对应：
--      CPU                  （窗口 1）
--      锁等待               （窗口 2 + 3，LCK_M_* / 睡眠持锁者）
--      用户等待             （窗口 4，WAITFOR）
--      日志写               （窗口 5，WRITELOG）
--      用户 IO（读）        （窗口 6，PAGEIOLATCH_SH）
--      并行                 （窗口 7，CXPACKET/CXCONSUMER）
--      缓冲闩锁             （窗口 8，PAGELATCH_EX）
--      网络                 （窗口 9，ASYNC_NETWORK_IO）
--      内存                 （窗口 10，RESOURCE_SEMAPHORE，可能为空）
--    标线 = max vCores（来自实例探测 cpu_cores）
--    1 分钟后：SampleFlushJob 写 dbpilot_active_request_sample，
--    “最近 30 分钟”及更长视图开始出分钟聚合
---------------------------------------------------------------------------

---------------------------------------------------------------------------
-- ④ 清理（验证后取消注释执行）
---------------------------------------------------------------------------
-- DROP TABLE dbpilot_smoke.dbo.SmokeBlockOrders;
-- DROP TABLE dbpilot_smoke.dbo.SmokePerfRows;
-- DROP TABLE dbpilot_smoke.dbo.SmokeIoRows;
-- DROP TABLE dbpilot_smoke.dbo.SmokeLogRows;
-- DROP TABLE dbpilot_smoke.dbo.SmokeHotRows;
