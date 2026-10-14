namespace Thor.Api.Services;

/// <summary>The tenant-assets bucket (served by the frontend CloudFront distribution under <c>/tenant-assets/*</c>).</summary>
public sealed record TenantAssetsOptions(string BucketName);
