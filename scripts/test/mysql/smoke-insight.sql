-- smoke-insight.sql : 性能洞察页测试负载（MySQL）
-- 结构：前置 smoke-base.sql → ① 窗口脚本（每个窗口在独立 mysql 客户端 / Navicat
--        会话中执行该段；同一文件整跑会串行执行、混不出等待桶分层）→ ② 验证说明
--        → ③ 清理
-- 前置：performance_schema=ON（ACTIVE_REQUESTS 采样依赖 threads /
--        events_statements_current 可读）
--
-- MySQL 口径下 AAS 最多 cpu / lock / other 三层（数据源边界：PROCESSLIST_STATE
-- 无 SQL Server wait_type 的细分粒度，SS 版 11 窗口的 WRITELOG/THREADPOOL 等
-- 专有等待词汇无对等形态）。桶映射（WaitBuckets MySQL 词汇）：
--   桶      窗口     会话状态                  说明
--   ------  -------  -----------------------  --------------------------------
--   lock    1, 2     Waiting for row lock     与 smoke-blocking 同款链
--   cpu     3, 4     User sleep / Sending data 在跑即 CPU 桶；SLEEP 是用户态计时，
--                                                    状态不带 Waiting 前缀 → WaitType 空
--   other   5        Waiting for table flush  时机敏感：需重扫进行中执行 FLUSH
-- 判定链：状态不带 Waiting → cpu；Waiting for …lock（行/元数据/表级）→ lock；
--         其余 Waiting for… → other。

-- ---------------------------------------------------------------------------
-- ① 窗口脚本（每段在独立会话执行；按窗口编号依次起跑）
-- ---------------------------------------------------------------------------

-- 窗口 1（lock 根）：执行后保持空闲（不 COMMIT / 不关连接），持锁 1~2 分钟
USE dbpilot_smoke;
BEGIN;
SELECT * FROM smoke_block WHERE id = 1 FOR UPDATE;

-- 窗口 2（lock 受害者；被窗口 1 阻塞）
USE dbpilot_smoke;
UPDATE smoke_block SET v = v + 1 WHERE id = 1;

-- 窗口 3（cpu：User sleep，30 秒内轮询可见；SS 版 WAITFOR 归 userWait 桶，
--         MySQL SLEEP 语义对不上落 CPU 属定义边界）
USE dbpilot_smoke;
SELECT SLEEP(30);

-- 窗口 4（cpu：IO 重扫约 6s，可连跑数次填满观察窗）
USE dbpilot_smoke;
SELECT COUNT(*) FROM smoke_orders a JOIN smoke_orders b ON a.customer_id = b.customer_id;

-- 窗口 5（other：Waiting for table flush；时机敏感）——先起窗口 4 的重扫，
--         重扫进行中（约 6s 窗口内）在本会话立即执行，FLUSH 等表句柄即挂起；
--         若瞬时返回说明重扫已结束（错过时机），重跑一次即可
USE dbpilot_smoke;
FLUSH TABLES smoke_orders;

-- ---------------------------------------------------------------------------
-- ② 验证：窗口运行期间打开 DBPilot 性能洞察页（MySQL 实例，实时档 10s 轮询），
--    AAS 堆叠分层，样本对应：
--      锁等待  （窗口 1 + 2，Waiting for row lock / 睡眠持锁者）
--      CPU     （窗口 3 + 4，User sleep / 重扫执行——在跑即 CPU 桶）
--      其他    （窗口 5，Waiting for table flush——非锁 Waiting 状态的唯一稳定造法）
--    Load By SQL：受害 UPDATE 与 SLEEP(?) / JOIN 指纹可见（依赖 digest 参数已恢复）
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- ③ 清理（无需手动：窗口结束自然恢复；表数据留给后续复用）
-- ---------------------------------------------------------------------------
