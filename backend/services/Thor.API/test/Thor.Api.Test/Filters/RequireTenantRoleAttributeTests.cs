using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Thor.Api.Controllers.V1;
using Thor.Api.Filters;
using Thor.Api.Middleware;
using Thor.Api.Models;

namespace Thor.Api.Test.Filters;

public class RequireTenantRoleAttributeTests
{
    private static AuthorizationFilterContext Run(
        TenantRole minimum, string[]? callerGroups, Action<HttpRequest>? configureRequest = null)
    {
        var httpContext = new DefaultHttpContext();
        if (callerGroups is not null)
        {
            httpContext.Features.Set(new AuthenticatedCaller(Guid.NewGuid(), "user-1", callerGroups));
        }

        configureRequest?.Invoke(httpContext.Request);

        var context = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []);

        new RequireTenantRoleAttribute(minimum).OnAuthorization(context);
        return context;
    }

    // Comma-separated, since attribute arguments can't be string arrays here.
    private static string[] Split(string groups) => groups.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static void ShouldBeForbidden(AuthorizationFilterContext context) =>
        context.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

    [Fact]
    public void OnAuthorization_NoAuthenticatedCaller_Returns403()
    {
        // e.g. the attribute on a route CognitoAuthMiddleware doesn't gate
        ShouldBeForbidden(Run(TenantRole.User, callerGroups: null));
    }

    [Theory]
    [InlineData("")] // machine callers carry no groups
    [InlineData("auditors")]
    [InlineData("xadmins")] // substring of a role group must not match
    [InlineData("Admins")] // group names are case-sensitive
    [InlineData("admins-readonly,auditors")]
    [InlineData("analysts")] // below Administrator
    [InlineData("users,analysts")]
    public void OnAuthorization_BelowAdministrator_Returns403(string groups)
    {
        ShouldBeForbidden(Run(TenantRole.Administrator, Split(groups)));
    }

    [Theory]
    [InlineData("admins")]
    [InlineData("auditors,admins")]
    [InlineData("users,admins")]
    public void OnAuthorization_Administrator_LetsRequestThrough(string groups)
    {
        Run(TenantRole.Administrator, Split(groups)).Result.Should().BeNull();
    }

    [Theory]
    [InlineData(TenantRole.User, "users", true)]
    [InlineData(TenantRole.User, "analysts", true)]
    [InlineData(TenantRole.User, "admins", true)]
    [InlineData(TenantRole.Analyst, "users", false)]
    [InlineData(TenantRole.Analyst, "analysts", true)]
    [InlineData(TenantRole.Analyst, "admins", true)]
    [InlineData(TenantRole.Administrator, "analysts", false)]
    public void OnAuthorization_RolesNest(TenantRole minimum, string group, bool allowed)
    {
        var context = Run(minimum, [group]);

        if (allowed)
        {
            context.Result.Should().BeNull();
        }
        else
        {
            ShouldBeForbidden(context);
        }
    }

    [Fact]
    public void OnAuthorization_ForgedGroupsHeader_IsIgnored()
    {
        // A request that bypasses API Gateway can send any header; only the verified token counts.
        var context = Run(TenantRole.Administrator, ["users"],
            request => request.Headers["X-THOR-CALLER-GROUPS"] = "admins");

        ShouldBeForbidden(context);
    }

    [Theory]
    [InlineData(typeof(IdentityProvidersController))]
    [InlineData(typeof(ServiceProviderInfoController))]
    [InlineData(typeof(TenantBrandingController))]
    public void TenantSetupControllers_AreAdminOnly(Type controller)
    {
        controller.GetCustomAttributes(typeof(RequireTenantRoleAttribute), inherit: true)
            .Should().ContainSingle()
            .Which.Should().BeOfType<RequireTenantRoleAttribute>()
            .Which.Minimum.Should().Be(TenantRole.Administrator);
    }
}
