using DBPilot.AspNetCore.Extension;
using DBPilot.Core.Auth;
using DBPilot.Core.Providers;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// CLI：dotnet run --project samples/DBPilot.Sample.MySql -- --hash <密码>  → 输出 PBKDF2 哈希（写入 appsettings DBPilot:Auth:PasswordHash）
if (args.Length >= 2 && args[0] == "--hash")
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return 0;
}

try
{
    Log.Information("DBPilot.Sample.MySql 启动中…");

    var builder = WebApplication.CreateBuilder(args);

    // 组合根只写"本形态的硬口径"：平台库引擎 = MySQL（枚举写死）。
    // 其余开关（Web/Collector/InitSchema）AddDBPilot 自动读 DBPilot:Roles / DBPilot:AutoInitSchema 做底，
    // 预编译产物改 appsettings 即可切「1 采集器 + N Web」拓扑；库消费方一律写委托（AddDBPilot(o => o.WebOnly())）。
    // 监控引擎零代码：本形态只引用 DBPilot.MySql 包 → 只能接入 MySQL 实例（要监控 SQL Server 补引 DBPilot.SqlServer 包即可）。
    builder.AddDBPilot(o => o.PlatformEngine = DbpilotEngine.MySql);

    var app = builder.Build();
    app.UseDBPilot();
    app.Run();

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "DBPilot.Sample.MySql 启动失败");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>暴露给 WebApplicationFactory（集成测试起真实组合链的 Host）。</summary>
public partial class Program;
