using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DBPilot.Scenarios;

class OrderStatusService(string cs) : IJob
{
    public string Key => "blocking";
    public string Title => "订单状态维护";

    public int Seconds { get; set; } = 60;

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[状态维护] 开始，预计 {Seconds}s ...");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Seconds));

        var head = Task.Run(() => MainTask(cts.Token));
        var w1 = Task.Run(() => SyncTask("同步任务1", cts.Token));
        var w2 = Task.Run(() => SyncTask("同步任务2", cts.Token));
        await Task.WhenAll(head, w1, w2);
        Console.WriteLine("[状态维护] 完成。");
    }

    private async Task MainTask(CancellationToken ct)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await Db.ExecTx(tx, "UPDATE dbo.t_order SET status = UPPER('HELD') WHERE id = ABS(1);");
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        await tx.RollbackAsync(CancellationToken.None);
    }

    private async Task SyncTask(string name, CancellationToken ct)
    {
        int timeouts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(
                    "UPDATE dbo.t_order SET status = UPPER('WAIT') WHERE id = ABS(1);", conn)
                { CommandTimeout = 15 };
                var sw = Stopwatch.StartNew();
                await cmd.ExecuteNonQueryAsync(ct);
                if (sw.Elapsed.TotalSeconds > 2) timeouts++;
            }
            catch (SqlException) { timeouts++; }
            catch (OperationCanceledException) { break; }
        }
        Console.WriteLine($"           {name}: 超时 {timeouts} 次");
    }
}
