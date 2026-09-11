-- smoke-index-usage.sql : 索引使用率页测试负载（MySQL）
-- 结构：前置 smoke-base.sql（smoke_unused 表 + 从不查询的二级索引 ix_smoke_unused_v）
--        → ① 负载（灌数 + 写入，幂等）→ ② 验证说明 → ③ 清理（验证后取消注释执行）

USE dbpilot_smoke;

-- ---------------------------------------------------------------------------
-- ① 负载（灌数 + 写入；幂等：仅空表时插入）
-- ---------------------------------------------------------------------------
SET SESSION cte_max_recursion_depth = 100000;

INSERT INTO smoke_unused (v, w)
WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 30000)
SELECT CONCAT('val-', LPAD(n, 6, '0')), n % 100
FROM seq
WHERE (SELECT COUNT(*) FROM smoke_unused) = 0;

-- 只写不读：UPDATE 键列本身（ix_smoke_unused_v 的 fetch 会被维护性触碰，
-- 但 user lookups 语义的"读"为零）
UPDATE smoke_unused SET w = w + 1 WHERE v BETWEEN 'val-001000' AND 'val-002000';

-- ---------------------------------------------------------------------------
-- ② 验证：等一个采集拍（或页面「重新采集」）后打开索引使用率页（MySQL 实例）
--    PRIMARY：读 = 定位 UPDATE 的行扫描（COUNT/lookup 口径有数）、写 = 3 万级
--    ix_smoke_unused_v：读极低、fetch 含二级索引维护噪音（MySQL 口径：连不碰索引列的
--      UPDATE 都计 fetch +1/行，读计数永不归零属实测定论，非 bug）
--    判定口径（与 SQL Server 不同）：IsUnused"零读+写放大"在 MySQL 实际不可触发
--      （宁漏报不误报），看统计卡「查找<100 / 读占比<10%」人工判断；
--      操作列「禁用脚本」= ALTER ... ALTER INDEX ... INVISIBLE（8.0+ 可恢复，
--      采集端已支持生成）
--    快照行：碎片率/维护操作为 NULL 显示 '-'（无对等数据源，入口已隐藏属预期）
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- ③ 清理（验证后取消注释执行）
-- ---------------------------------------------------------------------------
-- DELETE FROM smoke_unused WHERE id > 0;
