using System.Net;
using System.Net.Http.Json;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thor.Api.Models;
using Thor.Api.Test.Controllers.V1;
using Thor.Api.Test.TestFixtures;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Test;

/// <summary>
/// Proves the SPA's pre-sign-in call reaches LoginConfigController through the real pipeline —
/// i.e. Program.cs exempts it from CognitoAuthMiddleware, which would otherwise 401 a request
/// that has no bearer token.
/// </summary>
[Collection(ThorApiHostCollection.Name)]
public class LoginConfigEndpointTests : IClassFixture<ThorApiWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LoginConfigEndpointTests(ThorApiWebApplicationFactory factory)
    {
        var masterDb = new InMemoryMasterDbContextFactory(Guid.NewGuid().ToString());
        LoginConfigControllerTests.SeedTenant(masterDb, "acme", TenantStatus.Active);

        // Never the real client: login-config reads the app client's IdPs from Cognito.
        var cognito = Substitute.For<IAmazonCognitoIdentityProvider>();
        cognito.DescribeUserPoolClientAsync(Arg.Any<DescribeUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolClientResponse
            {
                UserPoolClient = new UserPoolClientType { SupportedIdentityProviders = ["COGNITO", "Okta"] },
            });

        _client = factory.WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .ConfigureTestServices(services => services
                    .AddSingleton<IMasterDbContextFactory>(masterDb)
                    .AddSingleton(cognito)))
            .CreateClient();
    }

    [Fact]
    public async Task LoginConfig_WithoutBearerToken_PassesAuthGate()
    {
        var response = await _client.GetAsync("/v1/login-config/acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LoginConfigResponse>())
            .Should().BeEquivalentTo(new LoginConfigResponse("us-east-1_acmePool", "acme-client", "us-east-1", null, null, ["Okta"]));
    }

    [Fact]
    public async Task LoginConfig_UnknownSubdomain_Returns404Not401()
    {
        var response = await _client.GetAsync("/v1/login-config/missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
