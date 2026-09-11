using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DBPilot.Scenarios;

// 日结对账报表，每天跑
class ReportService(string cs) : IJob
{
    public string Key => "slow";
    public string Title => "日结对账报表";

    public int Iterations { get; set; } = 10;

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[报表] 开始，共 {Iterations} 期...");
        const string sql = """
            SELECT status, COUNT(*) AS cnt, SUM(dbo.fn_order_sign(customer_id)) AS checksum
            FROM dbo.t_order
            GROUP BY status
            OPTION (MAXDOP 1);
            """;
        for (int i = 1; i <= Iterations; i++)
        {
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            var sw = Stopwatch.StartNew();
            await Db.Exec(conn, sql);
            Console.WriteLine($"           第 {i}/{Iterations} 期：{sw.Elapsed.TotalSeconds:F1}s");
        }
        Console.WriteLine("[报表] 完成。");
    }
}
