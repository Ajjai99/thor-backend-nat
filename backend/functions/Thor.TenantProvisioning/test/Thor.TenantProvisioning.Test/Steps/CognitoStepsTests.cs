using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Thor.TenantProvisioning.Core;
using Thor.TenantProvisioning.Core.Abstractions;
using Thor.TenantProvisioning.Core.Models;
using Thor.TenantProvisioning.Core.Steps;

namespace Thor.TenantProvisioning.Test.Steps;

public sealed class CognitoStepsTests
{
    private readonly ICognitoProvisioner _cognito = Substitute.For<ICognitoProvisioner>();

    private ProvisionCognitoStep CreateProvisionStep() => new(
        _cognito,
        new CognitoSignInOptions(DomainPrefix: "thor-dev", BaseDomain: "dev.example.com"),
        new TenantRoutingOptions("proxy.example.aws", "us-east-1"),
        NullLogger<ProvisionCognitoStep>.Instance);

    [Fact]
    public async Task ProvisionCognito_creates_pool_group_domain_and_oauth_and_records_them()
    {
        _cognito.EnsureUserPoolAsync("thor-acme", "thor-acme-app", Arg.Any<CancellationToken>())
            .Returns(new CognitoPool("pool-123", "client-456"));
        _cognito.EnsureDomainAsync("pool-123", "thor-dev-acme", Arg.Any<CancellationToken>())
            .Returns("thor-dev-acme");

        var result = await CreateProvisionStep().RunAsync(TestState.Sample());

        result.UserPoolId.Should().Be("pool-123");
        result.AppClientId.Should().Be("client-456");
        result.CognitoDomain.Should().Be("thor-dev-acme.auth.us-east-1.amazoncognito.com");
        await _cognito.Received(1).EnsureAdminGroupAsync("pool-123", ProvisioningNames.AdminGroup, Arg.Any<CancellationToken>());
        await _cognito.Received(1).EnsureAppClientOAuthAsync(
            "pool-123", "client-456", "https://acme.dev.example.com/auth/callback", "https://acme.dev.example.com/",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCognito_records_the_domain_the_pool_already_has()
    {
        _cognito.EnsureUserPoolAsync("thor-acme", "thor-acme-app", Arg.Any<CancellationToken>())
            .Returns(new CognitoPool("pool-123", "client-456"));
        _cognito.EnsureDomainAsync("pool-123", "thor-dev-acme", Arg.Any<CancellationToken>())
            .Returns("hand-made-acme");

        var result = await CreateProvisionStep().RunAsync(TestState.Sample());

        result.CognitoDomain.Should().Be("hand-made-acme.auth.us-east-1.amazoncognito.com");
    }

    [Fact]
    public async Task ProvisionCognito_rejects_an_invalid_domain_prefix_before_touching_cognito()
    {
        var act = () => CreateProvisionStep().RunAsync(TestState.Sample() with { Subdomain = "amazonia" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reserved word 'amazon'*");
        await _cognito.DidNotReceiveWithAnyArgs().EnsureUserPoolAsync(default!, default!, default);
    }

    [Fact]
    public async Task CreateAdminUser_throws_when_pool_not_yet_provisioned()
    {
        var step = new CreateAdminUserStep(_cognito, NullLogger<CreateAdminUserStep>.Instance);

        var act = () => step.RunAsync(TestState.Sample());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAdminUser_creates_user_when_pool_present()
    {
        var step = new CreateAdminUserStep(_cognito, NullLogger<CreateAdminUserStep>.Instance);
        var state = TestState.Sample() with { UserPoolId = "pool-123" };

        await step.RunAsync(state);

        await _cognito.Received(1).EnsureAdminUserAsync(
            "pool-123", ProvisioningNames.AdminGroup, "admin@acme.example.com", Arg.Any<CancellationToken>());
    }
}
