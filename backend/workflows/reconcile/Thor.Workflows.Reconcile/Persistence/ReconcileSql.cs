using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Thor.DataLayer.Data;

namespace Thor.Workflows.Reconcile.Persistence;

/// <summary>
/// Raw SQL for the anti-joins and set-based updates EF has no translation for — PAI's <c>PaiSql</c>
/// convention: opens and closes around each call and joins the ambient transaction when there is one.
/// The command timeout is raised because several statements here run over a source's whole population,
/// and Npgsql's 30 s default is sized for single-row queries.
/// </summary>
internal static class ReconcileSql
{
    private const int CommandTimeoutSeconds = 300;

    public static async Task<int> ExecAsync(
        TenantDbContext db, string sql, IReadOnlyList<NpgsqlParameter> parameters, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = Command(db, sql, parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task<List<T>> QueryAsync<T>(
        TenantDbContext db, string sql, IReadOnlyList<NpgsqlParameter> parameters, Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = Command(db, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlCommand Command(TenantDbContext db, string sql, IReadOnlyList<NpgsqlParameter> parameters)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.AddRange(parameters.ToArray());
        return command;
    }
}
