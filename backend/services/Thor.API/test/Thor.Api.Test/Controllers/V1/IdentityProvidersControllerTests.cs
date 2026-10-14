using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.Api.Constants;
using Thor.Api.Controllers.V1;
using Thor.Api.Middleware;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.Api.Test.TestFixtures;
using Thor.DataConnectionManager.Exceptions;
using Thor.DataConnectionManager.Routing;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Test.Controllers.V1;

public class IdentityProvidersControllerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly IAmazonCognitoIdentityProvider _cognito = Substitute.For<IAmazonCognitoIdentityProvider>();
    private readonly ITenantRoutingResolver _routingResolver = Substitute.For<ITenantRoutingResolver>();
    private readonly InMemoryMasterDbContextFactory _masterDb = new(Guid.NewGuid().ToString());

    public IdentityProvidersControllerTests()
    {
        _routingResolver.ResolveAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantRouting { TenantId = TenantId, UserPoolId = "us-east-1_pool", AppClientId = "client-1" });
    }

    // The admin gate is RequireTenantRoleAttribute (see its own tests); these tests cover the
    // action bodies, which run only after that filter has let the request through.
    private IdentityProvidersController CreateController(string? tenantHeaderValue)
    {
        var service = new IdentityProviderService(
            _cognito, _routingResolver, _masterDb, new MasterConnectionInfo("localhost", "unused", "unused", "unused"),
            TimeProvider.System, NullLogger<IdentityProviderService>.Instance);
        var controller = new IdentityProvidersController(service, NullLogger<IdentityProvidersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Features.Set(new AuthenticatedCaller(TenantId, "admin-sub-1", ["admins"]));

        if (tenantHeaderValue is not null)
        {
            controller.Request.Headers[TenantConstants.TenantHeaderName] = tenantHeaderValue;
        }

        return controller;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task List_MissingOrInvalidTenantHeader_ReturnsBadRequest(string? tenantHeaderValue)
    {
        var result = await CreateController(tenantHeaderValue).List(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_UnknownTenant_ReturnsForbidden()
    {
        var unknownTenant = Guid.NewGuid();
        _routingResolver.ResolveAsync(unknownTenant, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TenantNotFoundException(unknownTenant));

        var result = await CreateController(unknownTenant.ToString()).List(CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Get_UnknownProvider_ReturnsNotFound()
    {
        _cognito.DescribeIdentityProviderAsync(Arg.Any<DescribeIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ResourceNotFoundException("missing"));

        var result = await CreateController(TenantId.ToString()).Get("Okta", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Create_InvalidRequest_ReturnsBadRequest()
    {
        var request = new CreateTenantIdentityProviderRequest("Okta", "Google", null, null, null, null);

        var result = await CreateController(TenantId.ToString()).Create(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().Be("ProviderType must be 'SAML' or 'OIDC'.");
    }

    [Fact]
    public async Task Create_DuplicateProvider_ReturnsConflict()
    {
        _cognito.CreateIdentityProviderAsync(Arg.Any<CreateIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateProviderException("exists"));
        var request = new CreateTenantIdentityProviderRequest(
            "Okta", "SAML", new SamlProviderSettings("https://idp.example.com/metadata", null, null, null), null, null, null);

        var result = await CreateController(TenantId.ToString()).Create(request, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201()
    {
        _cognito.CreateIdentityProviderAsync(Arg.Any<CreateIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateIdentityProviderResponse
            {
                IdentityProvider = new IdentityProviderType { ProviderName = "Okta", ProviderType = IdentityProviderTypeType.SAML },
            });
        var request = new CreateTenantIdentityProviderRequest(
            "Okta", "SAML", new SamlProviderSettings("https://idp.example.com/metadata", null, null, null), null, null, null);

        var result = await CreateController(TenantId.ToString()).Create(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        objectResult.Value.Should().BeOfType<TenantIdentityProviderResponse>()
            .Which.ProviderName.Should().Be("Okta");

        // The verified caller is recorded as the creator.
        await using var db = _masterDb.Create(new MasterConnectionInfo("localhost", "unused", "unused", "unused"));
        db.TenantIdentityProviders.Single().CreatedBySub.Should().Be("admin-sub-1");
    }

    [Fact]
    public async Task Delete_ExistingProvider_ReturnsNoContent()
    {
        _cognito.DescribeIdentityProviderAsync(Arg.Any<DescribeIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeIdentityProviderResponse
            {
                IdentityProvider = new IdentityProviderType { ProviderName = "Okta", ProviderType = IdentityProviderTypeType.SAML },
            });
        _cognito.DescribeUserPoolClientAsync(Arg.Any<DescribeUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolClientResponse { UserPoolClient = new UserPoolClientType { SupportedIdentityProviders = ["COGNITO", "Okta"] } });

        var result = await CreateController(TenantId.ToString()).Delete("Okta", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnableDisable_ExistingProvider_ReturnsNoContent(bool enable)
    {
        _cognito.DescribeIdentityProviderAsync(Arg.Any<DescribeIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeIdentityProviderResponse
            {
                IdentityProvider = new IdentityProviderType { ProviderName = "Okta", ProviderType = IdentityProviderTypeType.SAML },
            });
        _cognito.DescribeUserPoolClientAsync(Arg.Any<DescribeUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolClientResponse { UserPoolClient = new UserPoolClientType { SupportedIdentityProviders = ["COGNITO"] } });
        var controller = CreateController(TenantId.ToString());

        var result = enable
            ? await controller.Enable("Okta", CancellationToken.None)
            : await controller.Disable("Okta", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Enable_UnknownProvider_ReturnsNotFound()
    {
        _cognito.DescribeIdentityProviderAsync(Arg.Any<DescribeIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ResourceNotFoundException("missing"));

        var result = await CreateController(TenantId.ToString()).Enable("Okta", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
