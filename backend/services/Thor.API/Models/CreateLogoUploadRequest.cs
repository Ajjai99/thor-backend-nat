namespace Thor.Api.Models;

/// <summary>
/// Body of <c>POST v{version}/branding/logo/upload-url</c>. <see cref="ContentType"/> is
/// <c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c>, and the PUT to the returned URL must
/// send the same <c>Content-Type</c> header.
/// </summary>
public sealed record CreateLogoUploadRequest(string ContentType);
