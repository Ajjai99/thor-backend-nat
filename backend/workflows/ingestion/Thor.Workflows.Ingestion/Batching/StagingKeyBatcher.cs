using Npgsql;
using Thor.DataLayer.Data;
using Thor.Workflows.Ingestion.EdgeGate;

namespace Thor.Workflows.Ingestion.Batching;

/// <summary>
/// Walks one manifest's rows in a staging table in key order, handing back the next
/// <see cref="StagingKeyRange"/> of roughly the requested row count. Both names are internal constants.
/// </summary>
internal sealed class StagingKeyBatcher(string stagingTable, string naturalIdColumn)
{
    public async Task<StagingKeyRange?> NextRangeAsync(
        TenantDbContext db, Guid scanManifestId, StagingKey? after, int rows, CancellationToken cancellationToken)
    {
        var afterClause = after is null ? "" : $"AND (source_id, {naturalIdColumn}) > (@after_source, @after_id)";
        var from = $"FROM {stagingTable} WHERE scan_manifest_id = @scan_manifest_id {afterClause}";

        var bound = await QueryKeyAsync(db, $"SELECT source_id, {naturalIdColumn} {from} ORDER BY source_id, {naturalIdColumn} OFFSET @skip LIMIT 1",
            scanManifestId, after, rows - 1, cancellationToken);

        // Fewer than a full batch remains, so the batch is everything left.
        bound ??= await QueryKeyAsync(db, $"SELECT source_id, {naturalIdColumn} {from} ORDER BY source_id DESC, {naturalIdColumn} DESC LIMIT 1",
            scanManifestId, after, skip: null, cancellationToken);

        return bound is { } through ? new StagingKeyRange(after, through) : null;
    }

    private static async Task<StagingKey?> QueryKeyAsync(
        TenantDbContext db, string sql, Guid scanManifestId, StagingKey? after, int? skip, CancellationToken cancellationToken)
    {
        var parameters = new List<NpgsqlParameter> { new("scan_manifest_id", scanManifestId) };
        if (skip is { } skipRows)
        {
            parameters.Add(new NpgsqlParameter("skip", (long)skipRows));
        }

        if (after is { } afterKey)
        {
            parameters.Add(new NpgsqlParameter("after_source", afterKey.SourceId));
            parameters.Add(new NpgsqlParameter("after_id", afterKey.NaturalId));
        }

        var rows = await SqlExec.QueryAsync(db, sql, r => new StagingKey(r.GetGuid(0), r.GetString(1)), cancellationToken, [.. parameters]);
        return rows.Count == 0 ? null : rows[0];
    }
}
