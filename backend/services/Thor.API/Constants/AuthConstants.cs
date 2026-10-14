using Thor.Auth;

namespace Thor.Api.Constants;

/// <summary>Shared constants for the connector registration/refresh endpoints.</summary>
public static class AuthConstants
{
    public const string ApiKeyHeaderName = "x-task-api-key";

    /// <summary>Carries the refresh token for <c>POST /register/refresh</c>.</summary>
    public const string AuthorizationHeaderName = "Authorization";

    public const string BearerScheme = "Bearer ";

    public const string JwtIssuer = TokenIssuers.ThorTaskApi;

    /// <summary>Audience for tokens issued to authenticate against Thor.TaskApi (ADR §5.2).</summary>
    public const string TaskApiAudience = TokenAudiences.TaskApi;

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
}
