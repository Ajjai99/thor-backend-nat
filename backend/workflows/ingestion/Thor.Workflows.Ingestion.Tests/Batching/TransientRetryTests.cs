using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Thor.Workflows.Ingestion.Batching;
using Xunit;

namespace Thor.Workflows.Ingestion.Tests.Batching;

public sealed class TransientRetryTests
{
    private static PostgresException Failure(string sqlState) =>
        new("failure", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public async Task RunAsync_DeadlockOrSerializationFailure_RetriesUntilTheBatchSucceeds(string sqlState)
    {
        var attempts = 0;

        var result = await TransientRetry.RunAsync(() =>
        {
            attempts++;
            return attempts < 3 ? throw Failure(sqlState) : Task.FromResult("done");
        }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RunAsync_OtherPostgresError_IsNotRetried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<PostgresException>(() => TransientRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw Failure(PostgresErrorCodes.UniqueViolation);
        }, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RunAsync_DeadlockEveryAttempt_GivesUpAndRethrows()
    {
        var attempts = 0;

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => TransientRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw Failure(PostgresErrorCodes.DeadlockDetected);
        }, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, thrown.SqlState);
        Assert.Equal(4, attempts);
    }
}
