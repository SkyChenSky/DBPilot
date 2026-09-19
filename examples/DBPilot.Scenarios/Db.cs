using Microsoft.Data.SqlClient;

namespace DBPilot.Scenarios;

static class Db
{
    public static string MasterCs(string cs) =>
        new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" }.ConnectionString;

    public static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ExecTx(SqlTransaction tx, string sql)
    {
        await using var cmd = new SqlCommand(sql, tx.Connection!, tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
