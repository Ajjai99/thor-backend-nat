namespace Thor.Api.Models;

/// <summary>
/// A presigned, short-lived S3 PUT for a new logo. Uploading alone changes nothing: the logo is only
/// shown once <c>PUT v{version}/branding/logo</c> validates and publishes <see cref="UploadId"/>.
/// </summary>
public sealed record LogoUploadResponse(Guid UploadId, string UploadUrl, DateTimeOffset ExpiresAt);
