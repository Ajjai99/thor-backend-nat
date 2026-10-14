using Microsoft.Extensions.Logging.Abstractions;
using Thor.DataLayer.Models.Tenants;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Ingestion.Batching;
using Thor.Workflows.Ingestion.Models;
using Thor.Workflows.Ingestion.Staging;
using Xunit;

namespace Thor.Workflows.Ingestion.Tests.Staging;

public sealed class StagerChunkingTests
{
    private sealed class RecordingRepository<T> : IStagingRepository<T> where T : class
    {
        public List<int> ChunkSizes { get; } = [];

        public Task BulkInsertAsync(IEnumerable<T> rows, CancellationToken cancellationToken = default)
        {
            ChunkSizes.Add(rows.Count());
            return Task.CompletedTask;
        }
    }

    private static ParsedAccount Account(Guid sourceId, int i) => new(
        sourceId, 308, $"native-{i}", "user", true, "Name", "sam", "upn", "mail", "dom", "sid", false, false, new { });

    [Fact]
    public async Task StageAsync_MoreRowsThanTheBatchSize_InsertsInBoundedChunks()
    {
        var accounts = new RecordingRepository<StagingAccount>();
        var groups = new RecordingRepository<StagingGrp>();
        var stager = new Stager(
            accounts, groups, new RecordingRepository<StagingAsset>(), new RecordingRepository<StagingEntitlement>(), new RecordingRepository<StagingIdentity>(),
            NullLogger<Stager>.Instance, new IngestionBatchOptions(3));
        var sourceId = Guid.NewGuid();
        var batch = new IngestBatch(sourceId, Enumerable.Range(0, 7).Select(i => Account(sourceId, i)).ToList(), [], [], [], 0, 0);

        await stager.StageAsync(batch, Guid.NewGuid(), Guid.NewGuid(), batchSeq: 0);

        Assert.Equal([3, 3, 1], accounts.ChunkSizes);
        Assert.Empty(groups.ChunkSizes); // nothing to stage for an empty kind — no empty COPY
    }
}
