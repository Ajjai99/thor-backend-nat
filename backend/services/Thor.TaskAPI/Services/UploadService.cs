using Amazon.S3;
using Amazon.S3.Model;
using Thor.DataConnectionManager;
using Thor.DataLayer.Models.Tenants;
using Thor.DataLayer.Repositories;
using Thor.TaskApi.Constants;
using Thor.TaskApi.Models;

namespace Thor.TaskApi.Services;

/// <summary>
/// Generates tenant-scoped presigned S3 upload URLs. Per ADR §5.3, the presigned URL is the
/// primary isolation boundary for uploads: the tenant segment of the object key is hard-bound
/// to a tenant re-verified against the Master metadata store, never to caller-supplied input.
/// </summary>
public sealed class UploadService(IAmazonS3 s3Client, ITenantConnectionManager tenantConnectionManager, UploadOptions options, ILogger<UploadService> logger)
{
    public async Task<PresignedUploadResponse> CreatePresignedUploadUrlAsync(
        Guid tenantId, PresignedUploadRequest request, CancellationToken cancellationToken = default)
    {
        // tenantId comes from the trusted X-THOR-TENANT-ID header (set by the Lambda
        // authorizer); GetTenantDbContextAsync re-resolves routing against the Master
        // metadata store before opening the tenant DB, so an unprovisioned tenant still
        // fails closed here (ADR §5.4).
        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        var scanTask = await new ScanTaskRepository(db).GetByIdAsync(request.TaskId, cancellationToken)
            ?? throw new InvalidOperationException($"No scan task found for TaskId {request.TaskId}.");

        var fileName = $"data_{request.TaskId}_{request.SourceId}_{Guid.CreateVersion7()}";

        var objectKey = $"tenants/{tenantId}/uploads/{scanTask.ScanId}/{request.SourceId}/{fileName}";
        var now = DateTime.UtcNow;
        var expiresAtUtc = now.Add(UploadConstants.PresignedUrlLifetime);

        // Registered before the URL exists, so there is never an object in S3 without a row: the
        // scan-completion gate (Thor.ScanGate) waits on Pending rows and needs every file the
        // connector could still upload to be one of them.
        await new ScanFileRepository(db).RegisterPendingAsync(new ScanFile
        {
            Id = Guid.NewGuid(),
            ScanId = scanTask.ScanId,
            ScanTaskId = scanTask.Id,
            FileLocation = objectKey,
            Status = ScanFileStatus.Pending,
            UploadExpiresAt = expiresAtUtc,
            CreatedAt = now,
            UpdatedAt = now,
        }, cancellationToken);

        var presignedRequest = new GetPreSignedUrlRequest
        {
            BucketName = options.BucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expiresAtUtc,
            ContentType = request.ContentType,
        };

        var uploadUrl = await s3Client.GetPreSignedURLAsync(presignedRequest);

        logger.LogInformation("Generated presigned upload URL, expiring at {ExpiresAtUtc}", expiresAtUtc);

        return new PresignedUploadResponse(uploadUrl, objectKey, expiresAtUtc);
    }
}
