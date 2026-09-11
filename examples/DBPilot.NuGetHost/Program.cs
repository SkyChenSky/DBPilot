// 最小接入示例：引用 nuget.org 包（DBPilot.AspNetCore + 引擎包）后，两行接入全平台。
// 完整能力说明见仓库 README「库引用接入（NuGet）」/ "Embedding via NuGet"。
using DBPilot.AspNetCore.Extension;
using DBPilot.Core.Providers;

var builder = WebApplication.CreateBuilder(args);
builder.AddDBPilot(o =>
{
    // 平台库引擎无默认值，必须显式指定（也可写配置 DBPilot:PlatformEngine，自动读取）。
    // 可选值：SqlServer / MySql / PostgreSql / Sqlite（Sqlite 为嵌入式单文件形态）。
    o.PlatformEngine = DbpilotEngine.SqlServer;
    // o.WebOnly();   // 只当 Web 前端不采集时打开；默认 Web + Collector 双开
});

var app = builder.Build();
app.UseDBPilot();
app.Run();
