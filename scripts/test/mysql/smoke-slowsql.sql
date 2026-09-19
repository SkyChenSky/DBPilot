-- smoke-slowsql.sql : 慢日志页测试负载（MySQL，mysql.slow_log 表水位通道）
-- 结构：前置 smoke-base.sql（smoke_orders 表）→ ① 负载（四案例，整文件跑）
--        → ② 验证说明 → ③ 清理
-- 前置：实例侧 slow_query_log=ON + log_output 含 TABLE（RDS 实例默认即此形态，
--        long_query_time 通常 1s）
-- 注意 ①：阿里云 RDS 代理约每 3 分钟整体收割（TRUNCATE）mysql.slow_log——
--          脚本跑完先等 1~2 个采集拍（30s/拍）再查页面，个别事件可能落在收割边界
--          丢失属已知边界
-- 注意 ②：批文本参与 C# 归一化指纹——可执行语句行内不放注释（同 SQL Server 口径）
--
-- 慢语句载体 = 低基数列（status，5 个值）自连接：500M 组合对，实测约 6s——
-- 真实业务错误形态（低基数列做连接条件），确定性超 1s 阈值且不依赖缓存冷热。

USE dbpilot_smoke;

-- ---------------------------------------------------------------------------
-- ① 负载（四案例）
-- ---------------------------------------------------------------------------

-- 案例 1/2：同指纹两批（同文本重查询 → 归一化后同一指纹，统计 tab 聚合为 1 条 2 次）
SELECT COUNT(*) FROM smoke_orders a JOIN smoke_orders b ON a.status = b.status;
SELECT COUNT(*) FROM smoke_orders a JOIN smoke_orders b ON a.status = b.status;

-- 案例 3：不同指纹（SUM 投影形态，真实慢 SQL；同款低基数连接）
SELECT SUM(a.id) FROM smoke_orders a JOIN smoke_orders b ON a.status = b.status;

-- 案例 4：阈值内不采（二级索引等值快查询 <1s，不落 slow_log）
SELECT COUNT(*) FROM smoke_orders WHERE customer_id = 42;

-- ---------------------------------------------------------------------------
-- ② 验证：打开 DBPilot 慢日志页（MySQL 实例）明细/统计双 tab
--    明细：COUNT 连接两条（durationMs≈6000、rows_examined 大）+ SUM 连接一条
--    统计：COUNT 连接聚合执行次数 2 / SUM 连接聚合 1；customer_id 等值查询不出现
--    事件字段口径：eventTimeUtc 由服务器本地时间换算；AppName 为空
--    （slow_log 无程序名列）= 设计边界
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- ③ 清理（无需手动：slow_log 行由 RDS 代理周期收割，平台侧按 Retention 90 天清理）
-- ---------------------------------------------------------------------------
