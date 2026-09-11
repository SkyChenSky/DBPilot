using System.Data;
using Microsoft.Data.SqlClient;

namespace DBPilot.Scenarios;

class DataInitService(string cs) : IJob
{
    public string Key => "init";
    public string Title => "基础数据初始化";

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("[初始化] 开始...");

        var b = new SqlConnectionStringBuilder(cs);
        await using (var master = new SqlConnection(Db.MasterCs(cs)))
        {
            await master.OpenAsync(ct);
            await Db.Exec(master, $"IF DB_ID(N'{b.InitialCatalog}') IS NULL CREATE DATABASE [{b.InitialCatalog}];");
        }

        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync(ct);
            await Db.Exec(conn, """
                IF OBJECT_ID('dbo.t_order') IS NULL
                CREATE TABLE dbo.t_order(
                    id          INT IDENTITY PRIMARY KEY,
                    customer_id INT NOT NULL,
                    status      VARCHAR(10) NOT NULL,
                    amount      DECIMAL(10,2) NOT NULL,
                    note        VARCHAR(200) NOT NULL,
                    created_at  DATETIME2 NOT NULL);
                IF OBJECT_ID('dbo.t_account') IS NULL
                CREATE TABLE dbo.t_account(
                    id         INT PRIMARY KEY,
                    balance    INT NOT NULL,
                    updated_by VARCHAR(50) NOT NULL);
                """);
            await Db.Exec(conn, """
                CREATE OR ALTER FUNCTION dbo.fn_order_sign(@seed INT) RETURNS BIGINT
                AS
                BEGIN
                    DECLARE @i INT = 0, @x BIGINT = 0;
                    WHILE @i < 10
                    BEGIN
                        SET @x = (@x + CAST(@i AS BIGINT) * @seed) % 1000003;
                        SET @i += 1;
                    END
                    RETURN @x;
                END
                """);
            await Db.Exec(conn, "TRUNCATE TABLE dbo.t_order; TRUNCATE TABLE dbo.t_account;");
            await Db.Exec(conn, "INSERT INTO dbo.t_account VALUES (1,1000,N'sys'),(2,1000,N'sys');");

            var table = new DataTable();
            table.Columns.Add("customer_id", typeof(int));
            table.Columns.Add("status", typeof(string));
            table.Columns.Add("amount", typeof(decimal));
            table.Columns.Add("note", typeof(string));
            table.Columns.Add("created_at", typeof(DateTime));
            var rnd = new Random(42);
            for (int i = 0; i < 200_000; i++)
            {
                table.Rows.Add(
                    i % 20_000,
                    rnd.Next(3) == 0 ? "NEW" : "DONE",
                    (decimal)(rnd.NextDouble() * 1000),
                    $"ORDER-{i % 9973}-{rnd.Next(10000)}",
                    DateTime.UtcNow.AddMinutes(-rnd.Next(0, 60 * 24 * 365)));
            }
            using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "dbo.t_order", BatchSize = 50_000 };
            foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }
        Console.WriteLine("[初始化] 完成。");
    }
}
