using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Thor.Api.Constants;
using Thor.Api.Middleware;
using Thor.Api.Models;

namespace Thor.Api.Filters;

/// <summary>
/// Allows the request only when the caller holds <paramref name="minimum"/> or a role above it
/// (roles nest, see <see cref="TenantRole"/>). The caller's groups come from the
/// <see cref="AuthenticatedCaller"/> that <see cref="CognitoAuthMiddleware"/> stored from the
/// verified token — never from a request header. Fails closed: no stored caller (a route the
/// middleware doesn't gate) or no role group (machine callers) is a 403. An authorization filter so
/// it runs before model binding — a caller without the role learns nothing about request validation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTenantRoleAttribute(TenantRole minimum) : Attribute, IAuthorizationFilter
{
    public TenantRole Minimum { get; } = minimum;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var caller = context.HttpContext.Features.Get<AuthenticatedCaller>();

        if (caller is null || HighestRole(caller.Groups) < Minimum)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>The highest role among the caller's groups; <c>default</c> (no role) when none match.</summary>
    private static TenantRole HighestRole(IReadOnlyList<string> groups) =>
        groups.Select(RoleForGroup).DefaultIfEmpty().Max();

    // Exact, case-sensitive match: Cognito group names are case-sensitive.
    private static TenantRole RoleForGroup(string group) => group switch
    {
        TenantConstants.AdminGroup => TenantRole.Administrator,
        TenantConstants.AnalystGroup => TenantRole.Analyst,
        TenantConstants.UserGroup => TenantRole.User,
        _ => default,
    };
}
