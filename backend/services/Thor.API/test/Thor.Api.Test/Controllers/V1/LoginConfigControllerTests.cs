using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.Api.Controllers.V1;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.Api.Test.TestFixtures;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Test.Controllers.V1;

/// <summary>
/// Exercises LoginConfigController wired to a real LoginConfigService over an EF Core InMemory
/// Master DB: only an active tenant with routing yields its pool/client ids. The app client's
/// enabled IdPs come from a substituted Cognito client.
/// </summary>
public class LoginConfigControllerTests
{
    private static readonly MasterConnectionInfo ConnectionInfo = new("localhost", "unused", "unused", "unused");

    private readonly IAmazonCognitoIdentityProvider _cognito = Substitute.For<IAmazonCognitoIdentityProvider>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public LoginConfigControllerTests() => AppClientHasProviders(["COGNITO"]);

    private (LoginConfigController Controller, InMemoryMasterDbContextFactory Factory) CreateController()
    {
        var factory = new InMemoryMasterDbContextFactory(Guid.NewGuid().ToString());
        var service = new LoginConfigService(factory, ConnectionInfo, _cognito, _cache, NullLogger<LoginConfigService>.Instance);
        return (new LoginConfigController(service), factory);
    }

    private void AppClientHasProviders(List<string>? providers) =>
        _cognito.DescribeUserPoolClientAsync(Arg.Any<DescribeUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolClientResponse
            {
                UserPoolClient = new UserPoolClientType { SupportedIdentityProviders = providers },
            });

    internal static void SeedTenant(
        InMemoryMasterDbContextFactory factory, string subdomain, TenantStatus status, bool withRouting = true,
        string? cognitoDomain = null, string? logoObjectKey = null)
    {
        var tenantId = Guid.NewGuid();
        if (logoObjectKey is not null)
        {
            using var brandingDb = factory.Create(ConnectionInfo);
            brandingDb.TenantBrandings.Add(new TenantBranding { TenantId = tenantId, LogoObjectKey = logoObjectKey });
            brandingDb.SaveChanges();
        }

        using var db = factory.Create(ConnectionInfo);
        db.Tenants.Add(new Tenant
        {
            TenantId = tenantId,
            DisplayName = subdomain,
            Subdomain = subdomain,
            StatusId = (short)status,
            Routing = withRouting
                ? new TenantRouting
                {
                    TenantId = tenantId,
                    ClusterEndpoint = "tenant-proxy.local",
                    DatabaseName = $"tenant_{subdomain}",
                    Region = "us-east-1",
                    UserPoolId = $"us-east-1_{subdomain}Pool",
                    AppClientId = $"{subdomain}-client",
                    DbUser = $"tenant_{subdomain}_rw",
                    ReadOnlyDbUser = $"tenant_{subdomain}_ro",
                    CognitoDomain = cognitoDomain,
                }
                : null,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Get_ActiveTenant_ReturnsItsPoolClientAndRegion()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active);
        SeedTenant(factory, "other", TenantStatus.Active);

        var result = await controller.Get("acme", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(new LoginConfigResponse("us-east-1_acmePool", "acme-client", "us-east-1", null, null, []));
    }

    [Fact]
    public async Task Get_ReturnsCognitoDomainAndFederatedProvidersButNotLocalAccounts()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active, cognitoDomain: "thor-dev-acme.auth.us-east-1.amazoncognito.com");
        AppClientHasProviders(["COGNITO", "Okta", "Entra"]);

        var result = await controller.Get("acme", CancellationToken.None);

        var config = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<LoginConfigResponse>().Subject;
        config.CognitoDomain.Should().Be("thor-dev-acme.auth.us-east-1.amazoncognito.com");
        config.IdentityProviders.Should().Equal("Okta", "Entra");
        await _cognito.Received(1).DescribeUserPoolClientAsync(
            Arg.Is<DescribeUserPoolClientRequest>(r => r.UserPoolId == "us-east-1_acmePool" && r.ClientId == "acme-client"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_ReturnsTheLogoAsAPathOnTheTenantsHost()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active, logoObjectKey: "tenant-assets/logos/abc.png");
        SeedTenant(factory, "other", TenantStatus.Active, logoObjectKey: "tenant-assets/logos/other.png");

        var result = await controller.Get("acme", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LoginConfigResponse>()
            .Which.LogoUrl.Should().Be("/tenant-assets/logos/abc.png");
    }

    [Fact]
    public async Task Get_CachesTheProviderListPerPool()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active);

        await controller.Get("acme", CancellationToken.None);
        await controller.Get("acme", CancellationToken.None);

        await _cognito.Received(1).DescribeUserPoolClientAsync(Arg.Any<DescribeUserPoolClientRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_WhenCognitoFails_OffersLocalSignInOnly()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active);
        _cognito.DescribeUserPoolClientAsync(Arg.Any<DescribeUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TooManyRequestsException("throttled"));

        var result = await controller.Get("acme", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<LoginConfigResponse>().Which.IdentityProviders.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_UnknownSubdomain_NeverCallsCognito()
    {
        var (controller, _) = CreateController();

        await controller.Get("missing", CancellationToken.None);

        _cognito.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Get_UnknownSubdomain_ReturnsNotFound()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active);

        var result = await controller.Get("missing", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_TenantStillProvisioning_ReturnsNotFound()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Provisioning);

        var result = await controller.Get("acme", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_ActiveTenantWithoutRouting_ReturnsNotFound()
    {
        var (controller, factory) = CreateController();
        SeedTenant(factory, "acme", TenantStatus.Active, withRouting: false);

        var result = await controller.Get("acme", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
