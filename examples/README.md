# examples

三个使用示例，全部消费 nuget.org 官方发布的 DBPilot 包（不依赖本仓库源码工程引用）：

| 示例 | 说明 |
|---|---|
| **DBPilot.NuGetHost** | 最小接入宿主：`dotnet add package DBPilot.AspNetCore` + 引擎包 → `AddDBPilot` / `UseDBPilot` 两行嵌入全平台（Web 控制台 + 采集调度 + MCP Server）。复制 `appsettings.template.json` 为 `appsettings.json` 填连接串即可 `dotnet run` |
| **DBPilot.McpConsole** | 命令行 AI 诊断对话台：OpenAI 兼容端点（DeepSeek / 通义等）+ 连接 DBPilot 的 MCP Server，自然语言问诊（"最近一小时有没有变慢的 SQL？"） |
| **DBPilot.Scenarios** | 故障演练工程：在演示库上注入阻塞 / 死锁 / 慢 SQL 等故障，配合 DBPilot 页面与 AI 诊断做端到端演练 |

## DBPilot.NuGetHost

```bash
cd DBPilot.NuGetHost
cp appsettings.template.json appsettings.json   # 填平台库连接串
dotnet run                                      # → http://localhost:5000（默认账号见主 README）
```

想换平台库引擎改 `Program.cs` 的 `PlatformEngine`（SqlServer / MySql / PostgreSql / Sqlite）并换引对应引擎包；监控哪种引擎只由引用的引擎包决定，与平台库引擎两轴独立。

## DBPilot.McpConsole

```bash
cd DBPilot.McpConsole
# appsettings.json：填 Ai:ApiKey（OpenAI 兼容端点）与 DbPilot:McpApiKey（与宿主 DBPilot:Mcp:ApiKey 一致）
dotnet run
```

前提：某个 DBPilot 宿主已开启 MCP（`DBPilot:Modules:Mcp:Enabled=true` + ApiKey）。

## DBPilot.Scenarios

见 `DBPilot.Scenarios/README.md`（需要一台可建库的 SQL Server 演示实例，**不要对生产库运行**）。
