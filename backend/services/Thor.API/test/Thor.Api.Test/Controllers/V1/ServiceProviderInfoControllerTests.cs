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
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.Api.Test.Support;
using Thor.DataConnectionManager.Exceptions;
using Thor.DataConnectionManager.Routing;
using Thor.DataLayer.Models;

namespace Thor.Api.Test.Controllers.V1;

public class ServiceProviderInfoControllerTests
{
    private const string UserPoolId = "us-east-1_pool";
    private const string CognitoDomain = "thor-dev-acme.auth.us-east-1.amazoncognito.com";

    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly IAmazonCognitoIdentityProvider _cognito = Substitute.For<IAmazonCognitoIdentityProvider>();
    private readonly ITenantRoutingResolver _routingResolver = Substitute.For<ITenantRoutingResolver>();

    private void TenantHasDomain(string? cognitoDomain) =>
        _routingResolver.ResolveAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantRouting
            {
                TenantId = TenantId, UserPoolId = UserPoolId, AppClientId = "client-1", CognitoDomain = cognitoDomain,
            });

    // The admin gate is RequireTenantRoleAttribute (see its own tests); these tests cover the
    // action body, which runs only after that filter has let the request through.
    private ServiceProviderInfoController CreateController(string? tenantHeaderValue)
    {
        var controller = new ServiceProviderInfoController(
            new ServiceProviderInfoService(_cognito, _routingResolver),
            NullLogger<ServiceProviderInfoController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        if (tenantHeaderValue is not null)
        {
            controller.Request.Headers[TenantConstants.TenantHeaderName] = tenantHeaderValue;
        }

        return controller;
    }

    [Fact]
    public async Task Get_ReturnsEndpointsFromTheTenantsPoolAndDomain()
    {
        TenantHasDomain(CognitoDomain);
        using var certificate = TestCertificates.SelfSigned("CN=Cognito");
        _cognito.GetSigningCertificateAsync(
                Arg.Is<GetSigningCertificateRequest>(r => r.UserPoolId == UserPoolId), Arg.Any<CancellationToken>())
            .Returns(new GetSigningCertificateResponse { Certificate = Convert.ToBase64String(certificate.RawData) });

        var result = await CreateController(TenantId.ToString()).Get(CancellationToken.None);

        var info = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ServiceProviderInfoResponse>().Subject;
        info.SamlEntityId.Should().Be($"urn:amazon:cognito:sp:{UserPoolId}");
        info.SamlAcsUrl.Should().Be($"https://{CognitoDomain}/saml2/idpresponse");
        info.SamlSignOutUrl.Should().Be($"https://{CognitoDomain}/saml2/logout");
        info.OidcRedirectUri.Should().Be($"https://{CognitoDomain}/oauth2/idpresponse");
        info.SigningCertificate.Subject.Should().Be("CN=Cognito");
        info.SigningCertificate.Pem.Should().StartWith("-----BEGIN CERTIFICATE-----");
    }

    [Fact]
    public async Task Get_TenantWithoutCognitoDomain_ReturnsConflict()
    {
        TenantHasDomain(null);

        var result = await CreateController(TenantId.ToString()).Get(CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        await _cognito.DidNotReceiveWithAnyArgs().GetSigningCertificateAsync(default!, default);
    }

    [Fact]
    public async Task Get_UnknownTenant_ReturnsForbidden()
    {
        _routingResolver.ResolveAsync(TenantId, Arg.Any<CancellationToken>()).ThrowsAsync(new TenantNotFoundException(TenantId));

        var result = await CreateController(TenantId.ToString()).Get(CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task Get_MissingOrInvalidTenantHeader_ReturnsBadRequest(string? tenantHeaderValue)
    {
        var result = await CreateController(tenantHeaderValue).Get(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
