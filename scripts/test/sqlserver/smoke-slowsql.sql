-- smoke-slowsql.sql : 制造超过 1s 阈值的慢 SQL
-- 结构：① 无自建表（复用 smoke-deadlock.sql 的 SmokeDlA）→ ② 负载（四个案例）
--        → ③ 验证（DBPilot 慢SQL页：同指纹两批合并、阈值内案例不出现）
-- 用法：sqlcmd -S <host> -U <user> -P <pwd> -d dbpilot_smoke -i smoke-slowsql.sql -f 65001
-- 注意 1：GO 分批（一批 = 一个 sql_batch_completed 事件）；批文本参与指纹，
--         所以案例 1/2 的批内不能有注释/PRINT（只有字面量不同），
--         否则指纹不同。
-- 注意 2：批内嵌 EXEC sp_executesql 不产生 rpc_completed（RPC 事件仅来自
--         客户端驱动的参数化调用 —— 要测 rpc 请用客户端参数化调用）。
-- 案例 1：批约 2s，模板 A 字面量 111
-- 案例 2：批约 2s，模板 A 字面量 999（与案例 1 同指纹）
-- 案例 3：批约 1.5s，形态不同（不同指纹）
-- 案例 4：阈值内 300ms（不应被采集）
SET NOCOUNT ON;
USE dbpilot_smoke;
GO
SELECT COUNT(*) FROM SmokeDlA WHERE V = 111;
WAITFOR DELAY '00:00:02';
GO
SELECT COUNT(*) FROM SmokeDlA WHERE V = 999;
WAITFOR DELAY '00:00:02';
GO
SELECT TOP 10 * FROM SmokeDlA ORDER BY Id DESC;
WAITFOR DELAY '00:00:01.5';
GO
WAITFOR DELAY '00:00:00.3';
GO
