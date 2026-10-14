using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Thor.TenantProvisioning.Function.Aws;

namespace Thor.TenantProvisioning.Test.Aws;

public sealed class CognitoProvisionerTests
{
    private const string PoolId = "us-east-1_pool";
    private const string ClientId = "client-1";
    private const string CallbackUrl = "https://acme.dev.example.com/auth/callback";
    private const string LogoutUrl = "https://acme.dev.example.com/";

    private readonly IAmazonCognitoIdentityProvider _client = Substitute.For<IAmazonCognitoIdentityProvider>();

    private CognitoProvisioner CreateProvisioner() => new(_client, NullLogger<CognitoProvisioner>.Instance);

    [Fact]
    public async Task EnsureDomain_creates_the_domain_when_the_pool_has_none()
    {
        _client.DescribeUserPoolAsync(Arg.Any<DescribeUserPoolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolResponse { UserPool = new UserPoolType { Id = PoolId } });

        var prefix = await CreateProvisioner().EnsureDomainAsync(PoolId, "thor-dev-acme");

        prefix.Should().Be("thor-dev-acme");
        await _client.Received(1).CreateUserPoolDomainAsync(
            Arg.Is<CreateUserPoolDomainRequest>(r => r.UserPoolId == PoolId && r.Domain == "thor-dev-acme"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureDomain_keeps_an_existing_domain()
    {
        _client.DescribeUserPoolAsync(Arg.Any<DescribeUserPoolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolResponse { UserPool = new UserPoolType { Id = PoolId, Domain = "hand-made" } });

        var prefix = await CreateProvisioner().EnsureDomainAsync(PoolId, "thor-dev-acme");

        prefix.Should().Be("hand-made");
        await _client.DidNotReceiveWithAnyArgs().CreateUserPoolDomainAsync(default!, default);
    }

    [Fact]
    public async Task EnsureAppClientOAuth_adds_oauth_and_keeps_every_existing_setting()
    {
        GivenClient(new UserPoolClientType
        {
            UserPoolId = PoolId,
            ClientId = ClientId,
            ClientName = "thor-acme-app",
            ExplicitAuthFlows = ["ALLOW_USER_SRP_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"],
            SupportedIdentityProviders = ["COGNITO", "Okta"],
            CallbackURLs = ["http://localhost:5173/auth/callback"],
            RefreshTokenValidity = 30,
        });
        UpdateUserPoolClientRequest? sent = null;
        await _client.UpdateUserPoolClientAsync(
            Arg.Do<UpdateUserPoolClientRequest>(r => sent = r), Arg.Any<CancellationToken>());

        await CreateProvisioner().EnsureAppClientOAuthAsync(PoolId, ClientId, CallbackUrl, LogoutUrl);

        sent.Should().NotBeNull();
        sent!.AllowedOAuthFlowsUserPoolClient.Should().BeTrue();
        sent.AllowedOAuthFlows.Should().BeEquivalentTo(["code"]);
        sent.AllowedOAuthScopes.Should().BeEquivalentTo(["openid", "email", "profile"]);
        sent.CallbackURLs.Should().BeEquivalentTo(["http://localhost:5173/auth/callback", CallbackUrl]);
        sent.LogoutURLs.Should().BeEquivalentTo([LogoutUrl]);
        sent.SupportedIdentityProviders.Should().BeEquivalentTo(["COGNITO", "Okta"]);
        sent.ExplicitAuthFlows.Should().BeEquivalentTo(["ALLOW_USER_SRP_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"]);
        sent.RefreshTokenValidity.Should().Be(30);
    }

    [Fact]
    public async Task EnsureAppClientOAuth_enables_local_accounts_when_no_provider_is_set()
    {
        GivenClient(new UserPoolClientType { UserPoolId = PoolId, ClientId = ClientId });
        UpdateUserPoolClientRequest? sent = null;
        await _client.UpdateUserPoolClientAsync(
            Arg.Do<UpdateUserPoolClientRequest>(r => sent = r), Arg.Any<CancellationToken>());

        await CreateProvisioner().EnsureAppClientOAuthAsync(PoolId, ClientId, CallbackUrl, LogoutUrl);

        sent!.SupportedIdentityProviders.Should().BeEquivalentTo(["COGNITO"]);
    }

    [Fact]
    public async Task EnsureAppClientOAuth_is_a_no_op_when_already_configured()
    {
        GivenClient(new UserPoolClientType
        {
            UserPoolId = PoolId,
            ClientId = ClientId,
            AllowedOAuthFlowsUserPoolClient = true,
            AllowedOAuthFlows = ["code"],
            AllowedOAuthScopes = ["profile", "openid", "email"],
            CallbackURLs = [CallbackUrl],
            LogoutURLs = [LogoutUrl],
            SupportedIdentityProviders = ["COGNITO"],
        });

        await CreateProvisioner().EnsureAppClientOAuthAsync(PoolId, ClientId, CallbackUrl, LogoutUrl);

        await _client.DidNotReceiveWithAnyArgs().UpdateUserPoolClientAsync(default!, default);
    }

    private void GivenClient(UserPoolClientType client) =>
        _client.DescribeUserPoolClientAsync(
                Arg.Is<DescribeUserPoolClientRequest>(r => r.UserPoolId == PoolId && r.ClientId == ClientId),
                Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolClientResponse { UserPoolClient = client });
}
