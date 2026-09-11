# scripts 目录说明

按用途分三类：

| 目录 | 用途 | 内容 |
|---|---|---|
| `run/` | 运行脚本（Windows 双击即用） | `start.bat` 单进程启动 / `dev.bat` 前后端热更新调试 / `test.bat` 全流程检查 |
| `database/sqlserver/` | 平台库结构（SQL Server 方言） | `schema.sql`（GO 分批幂等，构建时嵌入 `DBPilot.Storage`，启动按 `DBPilot:PlatformEngine=sqlserver` 自动初始化；亦可 DBA 手工执行） |
| `database/mysql/` | 平台库结构（MySQL 8.0+ 方言） | `schema.sql`（CREATE TABLE IF NOT EXISTS 幂等 + 索引内联，启动按 `DBPilot:PlatformEngine=mysql` 自动初始化） |
| `test/sqlserver/` | 页面功能测试脚本（对被监控 SQL Server 实例执行） | 见下方 `test/sqlserver/` 脚本索引 |
| `test/mysql/` | 页面功能测试脚本（对被监控 MySQL 8.0+ 实例执行） | 见下方 `test/mysql/` 脚本索引 |

> 注意：`run/` 下脚本以 `%~dp0..\..` 定位仓库根，移动位置需同步修改。
> 约定：test/ 脚本统一使用测试库 `dbpilot_smoke`；注释用中文；统一四段结构
> **① 建表造数（文件头，幂等）→ ② 负载/窗口脚本（可执行；多会话窗口在独立会话中分段执行）→ ③ 验证（自检 SQL 或页面预期说明）→ ④ 清理（注释，验证后手动执行）**。
> 若用 sqlcmd 跑含中文注释的脚本须加 `-f 65001`（默认代码页 GBK 会误解码吞换行），SSMS 直接执行无此问题。
> **database/mysql/schema.sql 附加约定**：按分号切批，字符串与注释中一律不出现分号；索引内联进 CREATE TABLE（MySQL 无 CREATE INDEX IF NOT EXISTS）。

## test/ 脚本索引

### test/sqlserver/（SQL Server）

| 脚本 | 页面/功能 | 说明 |
|---|---|---|
| `smoke-index.sql` | 索引缺失 / 索引使用率 | 建库 dbpilot_smoke + 建表灌数；缺失索引 6 条建议 + 未使用索引 1 条（只写不读 1 万次）；**其余脚本的库/表基础** |
| `smoke-frag.sql` | 索引使用页·碎片扫描 | GUID 聚集 + 页分裂场景，两个索引 >1000 页、碎片率中/高 |
| `smoke-topsql.sql` | 实时 Top SQL | 三种负载（高频短查询/低频慢/重读），验证排序口径与指纹合并 |
| `smoke-blocking.sql` | 阻塞分析 | 双链多会话制造阻塞（A 链 4 层 / B 链 2 层）；建表段幂等，窗口语句可执行（独立会话分段跑） |
| `smoke-insight.sql` | 性能洞察 | CPU/锁/空闲/日志写/用户IO/并行/缓冲闩锁/网络/内存 多等待桶负载观察 AAS 堆叠（含 ~600MB 大表，准备约 1 分钟） |
| `smoke-deadlock.sql` | 死锁（趋势/列表） | 每锁类型一个死锁（KEY/RID/PAG/OBJ 四色趋势）；**建 SmokeDlA/B、SmokeRlC/D 基础表供 slowsql/multi/proc 复用** |
| `smoke-deadlock-multi.sql` | 死锁（三方环） | 3 会话环形等待（processCount=3，一次事件计 KEY+RID 两色）；依赖 smoke-deadlock 的表 |
| `smoke-deadlock-proc.sql` | 死锁（存储过程栈帧） | 过程内死锁，栈帧带 procname + 语句文本；依赖 smoke-deadlock 的表 |
| `smoke-slowsql.sql` | 慢 SQL | >1s 阈值四案例（同指纹两批/不同指纹一批/阈值内不采）；依赖 smoke-deadlock 的 SmokeDlA 表 |
| `smoke-plan.sql` | 执行计划（快照/变更） | 同指纹两版计划（Seek+KeyLookup → Scan），验证计划变更事件与计划树；两阶段间需等一次快照 tick（≤5 分钟） |

依赖链：`smoke-index.sql` 建库（所有脚本的前置，除非库已存在）；`smoke-deadlock.sql` 建死锁基础表（`smoke-deadlock-multi/proc`、`smoke-slowsql` 复用）；`smoke-blocking.sql` 建的 `SmokeBlockOrders` 同时被 `smoke-insight.sql` 的锁等待窗口复用。各脚本末尾注释含窗口操作与清理语句。

### test/mysql/（MySQL 8.0+，不含 MySQL 不支持的功能：死锁/计划变更/缺失索引/碎片）

| 脚本 | 页面/功能 | 说明 |
|---|---|---|
| `smoke-base.sql` | 前置基础 | 建库 dbpilot_smoke + 三表造数（订单 5 万行/只写不读索引表/阻塞表 3 行）；**其余 mysql 脚本的前置** |
| `smoke-topsql.sql` | Top SQL（digest 榜） | 三种负载：过程内循环 ×2000（体内语句不进 digest 榜——MySQL 通用边界，CALL 本身入榜）/ 低频慢查询 ×3 + 全表扫 ×2（直接语句，上榜验证）；需 mysql 客户端整文件跑（DELIMITER） |
| `smoke-slowsql.sql` | 慢 SQL（slow_log 表水位） | 四案例：同指纹两批 / JOIN 重查询 / 阈值内不采；RDS 代理 ~3 分钟收割 slow_log，跑完尽快看页面 |
| `smoke-blocking.sql` | 阻塞分析 | 双会话窗口：睡着拿锁头（FOR UPDATE 后空闲）+ 受害者 UPDATE；实时 tab + 历史留痕双验证 |
| `smoke-insight.sql` | 性能洞察（AAS） | lock 桶（睡着头双窗口）+ CPU 桶（User sleep / 重扫——在跑即 CPU）+ other 桶（table flush，时机敏感）；MySQL 无细粒度等待词汇，AAS 最多 cpu/lock/other 三层 |
| `smoke-index-usage.sql` | 索引使用率 | 灌数 3 万行 + 键列 UPDATE 只写不读；含 fetch 维护噪音口径说明（IsUnused 不触发，看读占比卡） |

**MySQL 脚本通用注意**：
- 执行：`mysql -h <被监控实例> -P 3306 -u <账号> -p --default-character-set=utf8mb4 < 脚本.sql`（Navicat 等客户端亦可，但含存储过程的脚本须整文件执行——DELIMITER 逐条跑会断）
- RDS 参数组前置：`performance_schema=ON`（会话/阻塞/TopSQL/洞察依赖）；TopSQL 榜还需 `performance_schema_max_digest_length=1024`（默认值，置 0 则 digest 全 NULL 榜单恒空）；阻塞头/完整 SQL 文本依赖 `performance_schema_max_sql_text_length=1024` + `events_statements_history_size>0`；**存储过程体内语句的可见性另受 `performance_schema_max_statement_stack` 门控**（RDS 曾置 1、社区版默认 10——深度 1 时嵌套语句连事件都不产生且运行期改仪器无效，须参数组改 10 + 重启；=10 后体内语句带 SQL_TEXT 进语句历史，但**任何配置下都不进 digest 榜**——sp/stmt 事件 DIGEST 恒 NULL 属 MySQL 通用边界；实测定论见 smoke-topsql.sql 头部）
- 死锁 / 计划变更 / 缺失索引 / 碎片无 MySQL 脚本——功能矩阵无对等数据源，前端入口已隐藏
- 依赖链：`smoke-base.sql` 建库（其余 mysql 脚本前置）；`smoke-insight.sql` 锁等待窗口复用 `smoke-base.sql` 的 smoke_block 表
