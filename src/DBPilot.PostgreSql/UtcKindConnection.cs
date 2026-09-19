using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace DBPilot.PostgreSql;

/// <summary>
/// Npgsql timestamptz 仅接受 Kind=Utc 的 DateTime——跨引擎落库（如 SQL Server/MySQL 实例读回的
/// UTC 校准墙钟值 Kind=Unspecified）会在写参数时被拒。本包装在命令执行前把 Unspecified 参数
/// 原地规约为 Utc（刻度不变，仅标注语义），平台库与监控两侧共用。
/// </summary>
internal sealed class UtcKindConnection(string connectionString) : DbConnection
{
    private readonly NpgsqlConnection _inner = new(connectionString);

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand()
        => new UtcKindCommand(_inner.CreateCommand());

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private sealed class UtcKindCommand(NpgsqlCommand inner) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }

        protected override DbConnection? DbConnection
        {
            get => inner.Connection;
            set => inner.Connection = value as NpgsqlConnection;
        }

        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction;
            set => inner.Transaction = value as NpgsqlTransaction;
        }

        protected override DbParameterCollection DbParameterCollection => inner.Parameters;

        public override void Cancel() => inner.Cancel();

        protected override DbParameter CreateDbParameter() => inner.CreateParameter();

        public override void Prepare() { NormalizeParameters(); inner.Prepare(); }

        public override int ExecuteNonQuery() { NormalizeParameters(); return inner.ExecuteNonQuery(); }
        public override object? ExecuteScalar() { NormalizeParameters(); return inner.ExecuteScalar(); }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        { NormalizeParameters(); return inner.ExecuteReader(behavior); }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken ct) { NormalizeParameters(); return inner.ExecuteNonQueryAsync(ct); }
        public override Task<object?> ExecuteScalarAsync(CancellationToken ct) { NormalizeParameters(); return inner.ExecuteScalarAsync(ct); }
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
        { NormalizeParameters(); return await inner.ExecuteReaderAsync(behavior, ct); }

        private void NormalizeParameters()
        {
            foreach (NpgsqlParameter? p in inner.Parameters)
            {
                if (p?.Value is DateTime { Kind: DateTimeKind.Unspecified } d)
                    p.Value = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            }
        }
    }
}
