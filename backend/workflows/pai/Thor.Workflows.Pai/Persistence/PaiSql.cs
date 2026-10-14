using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Thor.DataLayer.Data;

namespace Thor.Workflows.Pai.Persistence;

/// <summary>
/// Raw SQL for the statements EF has no translation for (<c>unnest</c> upserts, array overlap,
/// <c>DELETE … USING</c>). Opens and closes around each call and joins the ambient transaction when
/// there is one, the convention <c>TenantSqlExec</c> and Ownership's readers follow.
/// </summary>
internal static class PaiSql
{
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

    public static async Task<List<Guid>> GuidsAsync(
        TenantDbContext db, string sql, IReadOnlyList<NpgsqlParameter> parameters, CancellationToken cancellationToken) =>
        await QueryAsync(db, sql, parameters, reader => reader.GetGuid(0), cancellationToken);

    private static NpgsqlCommand Command(TenantDbContext db, string sql, IReadOnlyList<NpgsqlParameter> parameters)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters.ToArray());
        return command;
    }
}
