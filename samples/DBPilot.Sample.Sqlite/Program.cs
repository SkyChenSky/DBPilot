using DBPilot.AspNetCore.Extension;
using DBPilot.Core.Auth;
using DBPilot.Core.Providers;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// CLI：dotnet run --project samples/DBPilot.Sample.Sqlite -- --hash <密码>  → 输出 PBKDF2 哈希（写入 appsettings DBPilot:Auth:PasswordHash）
if (args.Length >= 2 && args[0] == "--hash")
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return 0;
}

try
{
    Log.Information("DBPilot.Sample.Sqlite 启动中…");

    var builder = WebApplication.CreateBuilder(args);

    // 组合根只写"本形态的硬口径"：平台库引擎 = SQLite（枚举写死）。
    // 嵌入式单进程契约：Web+采集同进程（Roles 双开默认即如此），WAL + busy_timeout 兜底并发；
    // 多进程 Roles（1 采集器 + N Web）对 SQLite 平台库不做支持（跨进程写竞争调优超出目标）。
    // 监控引擎零代码：本形态引用了双引擎包 → 可接入 SQL Server / MySQL 实例（减引用即收窄）。
    builder.AddDBPilot(o => o.PlatformEngine = DbpilotEngine.Sqlite);

    var app = builder.Build();
    app.UseDBPilot();
    app.Run();

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "DBPilot.Sample.Sqlite 启动失败");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>暴露给 WebApplicationFactory（集成测试起真实组合链的 Host）。</summary>
public partial class Program;
