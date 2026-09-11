using Microsoft.Data.SqlClient;

namespace DBPilot.Scenarios;

// 账户调账。先扣款再入账，顺序不能改，2018 年线上出过事故
class AccountService(string cs) : IJob
{
    public string Key => "deadlock";
    public string Title => "账户调账批处理";

    public int Rounds { get; set; } = 20;

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[调账] 批处理启动，共 {Rounds} 笔/通道...");

        var a = Task.Run(() => Channel("CH-A", 1, 2));
        var b = Task.Run(() => Channel("CH-B", 2, 1));
        int failed = (await a) + (await b);
        Console.WriteLine($"[调账] 批处理完成：成功 {Rounds * 2 - failed} 笔，失败 {failed} 笔已入重试队列。");
    }

    private async Task<int> Channel(string ch, int from, int to)
    {
        int failed = 0;
        for (int i = 0; i < Rounds; i++)
        {
            try
            {
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
                await Db.ExecTx(tx, $"""
                    UPDATE dbo.t_account
                    SET balance = balance + 1, updated_by = N'{ch}' WHERE id = {from};
                    """);
                await Task.Delay(Random.Shared.Next(30, 150));
                await Db.ExecTx(tx, $"""
                    UPDATE dbo.t_account
                    SET balance = balance - 1, updated_by = N'{ch}' WHERE id = {to};
                    """);
                await tx.CommitAsync();
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                failed++;
            }
        }
        return failed;
    }
}
