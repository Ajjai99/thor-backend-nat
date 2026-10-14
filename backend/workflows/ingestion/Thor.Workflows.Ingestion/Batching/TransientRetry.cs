using Microsoft.Extensions.Logging;
using Npgsql;

namespace Thor.Workflows.Ingestion.Batching;

/// <summary>Re-runs a whole batch transaction when Postgres aborts it with a deadlock or serialization failure.</summary>
internal static class TransientRetry
{
    private const int MaxAttempts = 4;
    private const string DeadlockDetected = PostgresErrorCodes.DeadlockDetected;
    private const string SerializationFailure = PostgresErrorCodes.SerializationFailure;

    public static async Task<T> RunAsync<T>(Func<Task<T>> batch, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await batch();
            }
            catch (PostgresException ex) when (ex.SqlState is DeadlockDetected or SerializationFailure && attempt < MaxAttempts)
            {
                logger.LogWarning(ex,
                    "Batch aborted with SQLSTATE {SqlState}; retrying (attempt {Attempt} of {MaxAttempts}).",
                    ex.SqlState, attempt + 1, MaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt + Random.Shared.Next(0, 200)), cancellationToken);
            }
        }
    }
}
