# DBPilot.Scenarios — 遗留系统故障定位演练

一个**伪装成真实老旧项目**的控制台工程（"XX-ERP 订单后台工具 v1.2，2018 年上线"）：
SQL 语句零注释标记、代码注释缺失甚至误导（比如调账类里那句"顺序不能改"恰恰就是死锁根源），
用来演练真实生产排查场景——**注释不清晰、不好维护的代码，如何靠 DBPilot MCP 定位到问题行**。

## 故障与业务操作映射（只有本 README 知道，代码里无痕迹）

| 菜单操作 / 命令 | 类 | 制造的故障 | SQL 特征（grep 用） |
|---|---|---|---|
| 1) 基础数据初始化 / `init` | `Services/DataInitService.cs` | 无（建表 t_order/t_account + 函数 fn_order_sign + 造数 20 万行） | — |
| 2) 账户调账批处理 / `deadlock` | `Services/AccountService.cs` | 死锁：两通道反向转账，X 锁互等成环 | `UPDATE dbo.t_account SET balance = balance + 1 ...` |
| 3) 订单状态维护 / `blocking` | `Services/OrderStatusService.cs` | 阻塞：主任务持锁挂住 + 两个同步任务排队超时 | `UPDATE dbo.t_order SET status = UPPER('HELD') ...` |
| 4) 日结对账报表 / `slow` | `Services/ReportService.cs` | 慢查询：逐行调用标量函数 fn_order_sign（约 2~3.5s/次） | `SELECT status, COUNT(*) AS cnt, SUM(dbo.fn_order_sign(...)` |

用法：`dotnet run`（交互菜单）或 `dotnet run -- deadlock|blocking|slow|all`。仅用于测试实例。

## 定位演练工作流（无标记版）

前提：目标实例已接入 DBPilot；Claude Code 已添加 dbpilot MCP。

1. 制造故障：`dotnet run -- all`（或单场景）
2. 在**本目录**启动 Claude Code，提问：

```
用 dbpilot 的 MCP 工具查一下：
1. 最近有没有死锁事件？各方执行的 SQL 原文是什么？
2. 当前阻塞树的头阻塞者在执行什么？
3. 慢 SQL 榜前几名的语句原文？

这个项目是个注释不清的老系统。把拿到的 SQL 原文（或其中的表名/函数名等特征片段）
在当前项目源码里搜索，定位到是哪个文件哪个方法写出来的，
并说明每类问题的根因和修复建议。
```

3. 预期定位路径（演练的就是这个）：
   - 死锁：`get_deadlock_detail` 的 inputbuf 原文 `UPDATE dbo.t_account SET balance = balance + 1, updated_by = N'CH-A' WHERE id = 1;`
     → 搜 `dbo.t_account` 或 `balance = balance + 1` → 命中 `AccountService.cs` Channel 方法
     → 看出 CH-A 与 CH-B 更新顺序相反，即注释里"顺序不能改"那句是根因
   - 阻塞：`get_blocking_current` 头/等待者 SQL `UPDATE dbo.t_order SET status = UPPER('HELD') WHERE id = ABS(1);`
     → 搜 `UPPER('HELD')` → 命中 `OrderStatusService.cs` MainTask——事务开着不提交干等
   - 慢查询：`get_slow_sql` 原文含 `dbo.fn_order_sign`
     → 搜 `fn_order_sign` → 命中 `ReportService.cs` 的报表 SQL + `DataInitService.cs` 的函数定义（WHILE 循环逐行执行）

## 为什么代码里保留 UPPER()/ABS() 这种怪写法

服务端"简单参数化"会把普通 UPDATE 在 DMV 里改写成 `set [status] = @1 WHERE [id]=@2` 模板，
SQL 原文与源码对不上、grep 断链。函数表达式可阻止参数化，让 DMV 里保留字面原文。
死锁（XE inputbuf）与慢 SQL（XE 原始批文本）不受参数化影响。

连接未设 ApplicationName，DMV 里程序名显示为默认的 `.Net SqlClient Data Provider`——
这也是遗留系统的真实常态：只能靠 SQL 原文 + host_name 定位。

## 已实测（RDS，2026-09-02）

改版前（带 demo: 标记版）：deadlock 20/20 落库、slow 38 条落慢日志、blocking chain_tree 含标记，
三场景全链路验证通过。改版（去标记/表改名 t_order 等）后：deadlock、slow、init 复测通过；
首次使用请先 `dotnet run -- init` 重建表结构。
