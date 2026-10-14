using Npgsql;

namespace Thor.Workflows.Ingestion.Batching;

/// <summary>A staged row's natural key: its source and the id it has within that source.</summary>
internal readonly record struct StagingKey(Guid SourceId, string NaturalId);

/// <summary>
/// A slice <c>(After, Through]</c> of a staging table's key space. A batch is a whole key range, so
/// every duplicate of a key lands in the same batch and per-key dedup stays correct.
/// </summary>
internal sealed record StagingKeyRange(StagingKey? After, StagingKey Through)
{
    public string Predicate(string naturalIdColumn)
    {
        var through = $"(source_id, {naturalIdColumn}) <= (@through_source, @through_id)";
        return After is null ? through : $"(source_id, {naturalIdColumn}) > (@after_source, @after_id) AND {through}";
    }

    /// <summary>Fresh parameters for <see cref="Predicate"/> — a parameter instance can belong to only one command.</summary>
    public NpgsqlParameter[] CreateParameters()
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("through_source", Through.SourceId),
            new("through_id", Through.NaturalId),
        };

        if (After is { } after)
        {
            parameters.Add(new NpgsqlParameter("after_source", after.SourceId));
            parameters.Add(new NpgsqlParameter("after_id", after.NaturalId));
        }

        return [.. parameters];
    }
}
