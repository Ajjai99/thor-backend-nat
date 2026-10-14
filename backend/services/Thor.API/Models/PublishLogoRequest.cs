namespace Thor.Api.Models;

/// <summary>Body of <c>PUT v{version}/branding/logo</c>: publishes an uploaded logo, replacing any current one.</summary>
public sealed record PublishLogoRequest(Guid UploadId);
