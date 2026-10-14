using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.DataConnectionManager.Routing;

namespace Thor.Api.Services;

/// <summary>
/// Backs <c>v{version}/sso/service-provider</c>: the values a tenant admin registers in their IdP.
/// All are derived from the verified tenant's tenant_routing row (pool id and Cognito domain) —
/// nothing comes from the request.
/// </summary>
public sealed class ServiceProviderInfoService(
    IAmazonCognitoIdentityProvider cognito,
    ITenantRoutingResolver tenantRoutingResolver)
{
    public async Task<ServiceProviderInfoResponse> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);
        if (string.IsNullOrEmpty(routing.CognitoDomain))
        {
            throw new SsoNotConfiguredException();
        }

        var signing = await cognito.GetSigningCertificateAsync(
            new GetSigningCertificateRequest { UserPoolId = routing.UserPoolId }, cancellationToken);
        using var certificate = SamlCertificateParser.Parse(signing.Certificate);

        var domain = $"https://{routing.CognitoDomain}";
        return new ServiceProviderInfoResponse(
            SamlEntityId: $"urn:amazon:cognito:sp:{routing.UserPoolId}",
            SamlAcsUrl: $"{domain}/saml2/idpresponse",
            SamlSignOutUrl: $"{domain}/saml2/logout",
            OidcRedirectUri: $"{domain}/oauth2/idpresponse",
            SigningCertificate: SamlCertificateParser.ToResponse(certificate));
    }
}
