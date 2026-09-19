-- smoke-topsql.sql : 实时 Top SQL 测试负载（MySQL digest 榜）
-- 结构：前置 smoke-base.sql（smoke_orders 表）→ ① 负载（三形态，整文件跑）
--        → ② 验证说明 → ③ 清理（验证后取消注释执行）
-- 执行：mysql 客户端整文件跑（存储过程需 DELIMITER，Navicat 逐条执行会断）
-- 前置：RDS 参数组 performance_schema=ON +
--       performance_schema_max_digest_length=1024（置 0 时 digest 全 NULL 榜单恒空）+
--       performance_schema_max_sql_text_length=1024（真实样例文本）
--
-- ⚠ 存储过程体内语句的可见性定论（阿里云 RDS 8.0.36 实测，2026-09-08）：
--   ① digest 榜（events_statements_summary_by_digest）在任何配置下都只聚合顶层语句——
--      statement/sp/stmt 事件的 DIGEST/DIGEST_TEXT 恒 NULL（statement_stack=10 实锤），
--      过程体内语句永不进 Top SQL 榜，属 MySQL 通用设计边界而非 RDS 参数问题；
--      官方口径查过程内语句用 events_statements_history（CALL 本身照常入榜）；
--   ② statement_stack 门控的是语句历史可见性：=1 时嵌套语句连事件都不产生
--      （运行期开 statement/sp/* 仪器也无效，RDS 曾置 1，参数组改 10 + 重启已生效）；
--      =10 时过程体内语句带 SQL_TEXT 进 events_statements_history——利好阻塞
--      "睡着拿锁头"最后语句补查等语句上下文场景。
--
-- 场景模拟（三种负载特征，验证不同排序口径）：
--   1. 高频参数化短查询（过程内 ×2000）→ 执行次数口径（CALL 以 1 次入榜属预期）
--   2. 低频慢查询（SLEEP(1) ×3）        → 平均耗时口径
--   3. 重读全表扫（×2）                  → 逻辑读口径（status 无索引 rows_examined 高）

USE dbpilot_smoke;

-- ---------------------------------------------------------------------------
-- ① 负载（三形态）
-- ---------------------------------------------------------------------------

-- 形态一：高频参数化短查询 ×2000（体内语句不进 digest 榜——MySQL 通用边界见头部 ①；
--         体内语句可在 events_statements_history 观察（statement_stack=10 时带 SQL_TEXT））
UPDATE performance_schema.setup_instruments SET ENABLED='YES', TIMED='YES' WHERE NAME LIKE 'statement/sp/%';
DROP PROCEDURE IF EXISTS smoke_topsql_load;
DELIMITER $$
CREATE PROCEDURE smoke_topsql_load()
BEGIN
  DECLARE i INT DEFAULT 0;
  WHILE i < 2000 DO
    SELECT COUNT(*) FROM smoke_orders WHERE customer_id = i;
    SET i = i + 1;
  END WHILE;
END$$
DELIMITER ;

CALL smoke_topsql_load();

-- 形态二：低频慢查询 ×3（直接语句；SLEEP 计时入 digest，验证平均耗时口径）
SELECT SLEEP(1);
SELECT SLEEP(1);
SELECT SLEEP(1);

-- 形态三：重读全表扫 ×2（直接语句；status 无索引 → rows_examined 高，逻辑读榜首候选）
SELECT SUM(amount) FROM smoke_orders WHERE status = 3;
SELECT SUM(amount) FROM smoke_orders WHERE status = 3;

-- ---------------------------------------------------------------------------
-- ② 验证：等 60s 采集拍后打开 DBPilot Top SQL 页（MySQL 实例）查询总榜
--    平均耗时口径：SELECT SLEEP(?) 1000ms 居首（当前 RDS 即可验证）
--    逻辑读口径  ：SELECT SUM(amount) ... WHERE status = ?（全表扫，当前 RDS 即可验证）
--    执行次数口径：CALL smoke_topsql_load() 以 1 次入榜属预期（体内 2000 次循环语句
--      不进 digest 榜——MySQL 通用边界，见头部 ①；体内语句走 events_statements_history）
--    平台自监控（SHOW GLOBAL STATUS 等）与 RDS 内部语句已被排除规则挡住，不应上榜
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- ③ 清理（验证后取消注释执行）
-- ---------------------------------------------------------------------------
-- DROP PROCEDURE IF EXISTS smoke_topsql_load;
