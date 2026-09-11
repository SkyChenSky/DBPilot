// ============================================================================
// XX-ERP 订单后台工具 v1.2（2018 上线）
// 调账 / 订单状态维护 / 日结报表，运维手动跑
// ============================================================================

using System.Text.Json;
using DBPilot.Scenarios;

// 优先读 appsettings.Local.json（gitignore，放真实凭据），不存在再读模板 appsettings.json
var configPath = File.Exists("appsettings.Local.json") ? "appsettings.Local.json" : "appsettings.json";
var config = JsonSerializer.Deserialize<AppConfig>(
        await File.ReadAllTextAsync(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"{configPath} 解析失败");
if (string.IsNullOrWhiteSpace(config.ConnectionString))
    throw new InvalidOperationException($"请在 {configPath} 配置数据库连接串");

string cs = config.ConnectionString;

if (args.Length > 0)
{
    await Run(args[0].ToLowerInvariant());
    return 0;
}

while (true)
{
    var jobs = CreateJobs();
    Console.WriteLine();
    Console.WriteLine("==== XX-ERP 订单后台工具 ====");
    for (int i = 0; i < jobs.Length; i++)
        Console.WriteLine($"  {i + 1}) {jobs[i].Title}");
    Console.WriteLine($"  {jobs.Length + 1}) 全部跑一遍");
    Console.WriteLine("  0) 退出");
    Console.Write("选择: ");
    var choice = Console.ReadLine()?.Trim();
    if (choice == "0" || string.IsNullOrEmpty(choice)) break;
    try { await Run(choice); }
    catch (Exception ex) { Console.WriteLine($"[出错] {ex.Message}"); }
}

return 0;

IJob[] CreateJobs() =>
[
    new DataInitService(cs),
    new AccountService(cs),
    new OrderStatusService(cs),
    new ReportService(cs),
];

async Task Run(string cmd)
{
    var jobs = CreateJobs();
    if (cmd == (jobs.Length + 1).ToString() || cmd == "all")
    {
        await new AccountService(cs) { Rounds = 10 }.RunAsync();
        await new OrderStatusService(cs) { Seconds = 30 }.RunAsync();
        await new ReportService(cs) { Iterations = 5 }.RunAsync();
        return;
    }
    var target = int.TryParse(cmd, out var n) && n >= 1 && n <= jobs.Length
        ? jobs[n - 1]
        : jobs.FirstOrDefault(j => j.Key == cmd);
    if (target == null) { Console.WriteLine($"无效选择: {cmd}"); return; }
    await target.RunAsync();
}
