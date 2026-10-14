using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.DataConnectionManager.Routing;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Services;

/// <summary>
/// Backs <c>v{version}/branding/logo</c>: a tenant admin's login-page logo.
/// <para>
/// Upload is two-step so nothing unvalidated is ever served. The admin PUTs to a presigned URL under
/// <c>pending/{tenantId}/</c>, a prefix CloudFront can't read. Publishing then checks the object's
/// size and leading bytes (the content type comes from those bytes, never from the uploader) and
/// copies it to a fresh key under <c>tenant-assets/logos/</c>. Keys are built only from the verified
/// tenant id and server-generated ids, never from the request. A fresh key per logo lets CloudFront
/// cache logos indefinitely; the replaced logo is deleted.
/// </para>
/// </summary>
public sealed class TenantBrandingService(
    IAmazonS3 s3,
    IMasterDbContextFactory dbContextFactory,
    MasterConnectionInfo connectionInfo,
    ITenantRoutingResolver tenantRoutingResolver,
    TenantAssetsOptions options,
    TimeProvider timeProvider,
    ILogger<TenantBrandingService> logger)
{
    public const long MaxLogoBytes = 256 * 1024;

    private const string LogoKeyPrefix = "tenant-assets/logos/";
    private static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The URL path a logo is served at on the tenant's SPA host.</summary>
    public static string LogoUrl(string logoObjectKey) => "/" + logoObjectKey;

    public async Task<LogoUploadResponse> CreateUploadAsync(
        Guid tenantId, CreateLogoUploadRequest request, CancellationToken cancellationToken)
    {
        if (!LogoImageFormat.All.Any(f => string.Equals(f.ContentType, request.ContentType, StringComparison.Ordinal)))
        {
            throw new InvalidLogoException(
                $"contentType must be one of {string.Join(", ", LogoImageFormat.All.Select(f => f.ContentType))}.");
        }

        // Fails closed (TenantNotFoundException) for a tenant the Master DB doesn't know.
        await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        var uploadId = Guid.NewGuid();
        var expiresAt = timeProvider.GetUtcNow().Add(UploadUrlLifetime);
        var uploadUrl = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = options.BucketName,
            Key = PendingKey(tenantId, uploadId),
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = request.ContentType,
        });

        logger.LogInformation("Issued logo upload {UploadId} for tenant {TenantId}, expiring at {ExpiresAt}",
            uploadId, tenantId, expiresAt);
        return new LogoUploadResponse(uploadId, uploadUrl, expiresAt);
    }

    public async Task<LogoResponse> PublishAsync(Guid tenantId, PublishLogoRequest request, CancellationToken cancellationToken)
    {
        await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        var pendingKey = PendingKey(tenantId, request.UploadId);
        var (format, etag) = await ValidatePendingAsync(pendingKey, cancellationToken);

        var logoKey = $"{LogoKeyPrefix}{Guid.NewGuid():N}.{format.Extension}";
        var copy = new CopyObjectRequest
        {
            SourceBucket = options.BucketName,
            SourceKey = pendingKey,
            DestinationBucket = options.BucketName,
            DestinationKey = logoKey,
            // Only the bytes just validated: a second PUT to the presigned URL since then fails the copy.
            ETagToMatch = etag,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = format.ContentType,
        };
        copy.Headers.CacheControl = "public, max-age=31536000, immutable";
        await s3.CopyObjectAsync(copy, cancellationToken);

        string? previousKey;
        try
        {
            previousKey = await SaveLogoKeyAsync(tenantId, logoKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording logo {LogoKey} for tenant {TenantId} failed; deleting the copy", logoKey, tenantId);
            await DeleteBestEffortAsync(logoKey);
            throw;
        }

        await DeleteBestEffortAsync(pendingKey);
        if (previousKey is not null)
        {
            await DeleteBestEffortAsync(previousKey);
        }

        logger.LogInformation("Published logo {LogoKey} ({ContentType}) for tenant {TenantId}", logoKey, format.ContentType, tenantId);
        return new LogoResponse(LogoUrl(logoKey));
    }

    public async Task DeleteAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        await using var db = dbContextFactory.Create(connectionInfo);
        var branding = await db.TenantBrandings.SingleOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken)
            ?? throw new LogoNotFoundException();

        db.TenantBrandings.Remove(branding);
        await db.SaveChangesAsync(cancellationToken);

        await DeleteBestEffortAsync(branding.LogoObjectKey);
        logger.LogInformation("Removed logo {LogoKey} for tenant {TenantId}", branding.LogoObjectKey, tenantId);
    }

    /// <summary>Checks the pending upload exists, is within the size limit and is a supported image.</summary>
    private async Task<(LogoImageFormat Format, string ETag)> ValidatePendingAsync(string pendingKey, CancellationToken cancellationToken)
    {
        GetObjectMetadataResponse metadata;
        try
        {
            metadata = await s3.GetObjectMetadataAsync(options.BucketName, pendingKey, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidLogoException("No upload found for this uploadId; it may have expired.");
        }

        if (metadata.ContentLength > MaxLogoBytes)
        {
            await DeleteBestEffortAsync(pendingKey);
            throw new InvalidLogoException($"The logo must be at most {MaxLogoBytes / 1024} KB.");
        }

        var header = new byte[LogoImageFormat.SignatureLength];
        int read;
        using (var response = await s3.GetObjectAsync(new GetObjectRequest
               {
                   BucketName = options.BucketName,
                   Key = pendingKey,
                   ByteRange = new ByteRange(0, LogoImageFormat.SignatureLength - 1),
                   EtagToMatch = metadata.ETag,
               }, cancellationToken))
        {
            read = await response.ResponseStream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        var format = LogoImageFormat.Detect(header.AsSpan(0, read));
        if (format is null)
        {
            await DeleteBestEffortAsync(pendingKey);
            throw new InvalidLogoException("The logo must be a PNG, JPEG or WebP image.");
        }

        return (format, metadata.ETag);
    }

    /// <summary>Sets the tenant's logo key, returning the one it replaces (if any).</summary>
    private async Task<string?> SaveLogoKeyAsync(Guid tenantId, string logoKey, CancellationToken cancellationToken)
    {
        await using var db = dbContextFactory.Create(connectionInfo);
        var now = timeProvider.GetUtcNow();

        var branding = await db.TenantBrandings.SingleOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);
        var previousKey = branding?.LogoObjectKey;
        if (branding is null)
        {
            db.TenantBrandings.Add(new TenantBranding { TenantId = tenantId, LogoObjectKey = logoKey, UpdatedAt = now });
        }
        else
        {
            branding.LogoObjectKey = logoKey;
            branding.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return previousKey;
    }

    private async Task DeleteBestEffortAsync(string key)
    {
        try
        {
            // Not the request's token: cleanup must still run if the request was cancelled.
            await s3.DeleteObjectAsync(options.BucketName, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A leftover pending object expires via the bucket lifecycle rule; a leftover logo stays
            // reachable at its unguessable key until removed by hand.
            logger.LogError(ex, "Deleting tenant asset {ObjectKey} failed", key);
        }
    }

    private static string PendingKey(Guid tenantId, Guid uploadId) => $"pending/{tenantId}/{uploadId}";
}
