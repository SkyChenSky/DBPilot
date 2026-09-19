-- smoke-base.sql : MySQL 冒烟库基础（建库 dbpilot_smoke + 三表造数，其余 mysql 冒烟脚本的前置）
-- 结构：① 建库建表（幂等）→ ② 灌数（幂等：仅空表时插入）→ ③ 验证 → ④ 清理（验证后取消注释执行）
-- 执行：mysql -h <被监控实例> -P 3306 -u <账号> -p --default-character-set=utf8mb4 < smoke-base.sql
-- 前置权限：CREATE DATABASE / CREATE TABLE / CREATE ROUTINE（RDS 控制台账号默认可配）
--
-- 三张表的分工：
--   smoke_orders：topsql / slowsql / insight 负载基础（5 万行，customer_id 带二级索引）
--   smoke_unused：index-usage 脚本用（只写不读的二级索引 ix_smoke_unused_v）
--   smoke_block ：blocking / insight 锁等待窗口用（3 行，行锁粒度）

-- ---------------------------------------------------------------------------
-- ① 建库建表（幂等）
-- ---------------------------------------------------------------------------
CREATE DATABASE IF NOT EXISTS dbpilot_smoke DEFAULT CHARACTER SET utf8mb4;
USE dbpilot_smoke;

-- 订单表：topsql / slowsql / insight 负载基础（5 万行，customer_id 带二级索引）
CREATE TABLE IF NOT EXISTS smoke_orders (
  id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  customer_id INT NOT NULL,
  status TINYINT NOT NULL,
  amount DECIMAL(12,2) NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_smoke_orders_customer (customer_id)
) ENGINE = InnoDB;

-- 只写不读的二级索引表：index-usage 脚本用（ix_smoke_unused_v 从不被查询）
CREATE TABLE IF NOT EXISTS smoke_unused (
  id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  v VARCHAR(64) NOT NULL,
  w INT NOT NULL DEFAULT 0,
  KEY ix_smoke_unused_v (v)
) ENGINE = InnoDB;

-- 阻塞表：blocking / insight 锁等待窗口用（3 行，行锁粒度）
CREATE TABLE IF NOT EXISTS smoke_block (
  id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  v INT NOT NULL DEFAULT 0,
  w INT NOT NULL DEFAULT 0
) ENGINE = InnoDB;

-- ---------------------------------------------------------------------------
-- ② 灌数（幂等：仅空表时插入；递归 CTE 上限需临时调大，默认 1000 不够 5 万）
-- ---------------------------------------------------------------------------
SET SESSION cte_max_recursion_depth = 100000;

INSERT INTO smoke_orders (customer_id, status, amount, created_at)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 50000)
SELECT n % 5000, n % 5, ROUND(100 + (n % 997) * 1.37, 2), NOW() - INTERVAL (n % 1440) MINUTE
FROM seq
WHERE (SELECT COUNT(*) FROM smoke_orders) = 0;

INSERT INTO smoke_block (v, w)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 3)
SELECT 0, 0 FROM seq
WHERE (SELECT COUNT(*) FROM smoke_block) = 0;

-- ---------------------------------------------------------------------------
-- ③ 验证：orders_cnt 预期 50000、block_cnt 预期 3
--    （smoke_unused 的灌数在 index-usage 脚本内）
-- ---------------------------------------------------------------------------
SELECT (SELECT COUNT(*) FROM smoke_orders) AS orders_cnt,
       (SELECT COUNT(*) FROM smoke_block)  AS block_cnt;

-- ---------------------------------------------------------------------------
-- ④ 清理（验证后取消注释执行）
-- ---------------------------------------------------------------------------
-- DROP DATABASE IF EXISTS dbpilot_smoke;
