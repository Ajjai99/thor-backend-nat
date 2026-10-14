namespace Thor.Api.Models;

/// <summary>
/// The published logo. <see cref="LogoUrl"/> is a path on the tenant's own SPA host
/// (e.g. <c>/tenant-assets/logos/….png</c>), the same value <c>login-config</c> returns.
/// </summary>
public sealed record LogoResponse(string LogoUrl);
