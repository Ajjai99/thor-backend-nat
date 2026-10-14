using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>The one bucket Thor.TaskApi presigns uploads into.</summary>
public sealed record UploadsBucketOptions(string BucketName);

/// <summary>HEADs an object in the uploads bucket; a 404 means it does not exist.</summary>
public sealed class S3UploadObjectProbe(IAmazonS3 s3Client, UploadsBucketOptions options) : IUploadObjectProbe
{
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = options.BucketName, Key = key }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
