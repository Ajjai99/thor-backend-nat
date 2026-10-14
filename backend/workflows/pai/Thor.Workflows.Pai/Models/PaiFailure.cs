namespace Thor.Workflows.Pai.Models;

/// <summary>What Step Functions writes to <c>$.Error</c> when a <c>Catch</c> fires. Both fields are optional: the shape depends on how the state failed.</summary>
public sealed record PaiFailureCause(string? Error, string? Cause)
{
    /// <summary>Both halves, truncated: a Lambda failure's Cause is a serialized stack trace, and the full one is in the execution history.</summary>
    public static string Describe(PaiFailureCause? cause, int maxLength)
    {
        if (cause is null)
        {
            return "no error detail was supplied";
        }

        var text = $"{cause.Error ?? "Unknown"}: {cause.Cause ?? "(no cause)"}";
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

/// <summary>What <see cref="Steps.PaiRecordFailureStep"/> is invoked with: the run's identity from <c>$.Request</c>, which every Catch preserves, and the cause.</summary>
public sealed record PaiFailureRequest(PaiRequest Request, PaiFailureCause? Error);

/// <summary>What <see cref="Steps.PaiRecordChunkFailureStep"/> is invoked with, on a Map item's Catch path: the item's own fields plus the error merged into it.</summary>
public sealed record PaiChunkFailureRequest(Guid TenantId, Guid? ScanId, Guid? RunId, int Offset, int Limit, PaiFailureCause? Error);
