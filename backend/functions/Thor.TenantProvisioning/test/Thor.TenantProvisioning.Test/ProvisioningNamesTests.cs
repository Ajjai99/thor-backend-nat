using FluentAssertions;
using Thor.TenantProvisioning.Core;

namespace Thor.TenantProvisioning.Test;

public sealed class ProvisioningNamesTests
{
    [Fact]
    public void CognitoDomainPrefix_joins_env_prefix_and_subdomain_in_lowercase() =>
        ProvisioningNames.CognitoDomainPrefix("thor-dev", "Acme").Should().Be("thor-dev-acme");

    [Theory]
    [InlineData("acme-aws-team", "aws")]
    [InlineData("amazonia", "amazon")]
    [InlineData("cognitoish", "cognito")]
    public void CognitoDomainPrefix_rejects_reserved_words(string subdomain, string reserved)
    {
        var act = () => ProvisioningNames.CognitoDomainPrefix("thor-dev", subdomain);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{reserved}'*");
    }

    [Theory]
    [InlineData("acme_corp")]
    [InlineData("acme-")]
    [InlineData("acme.corp")]
    public void CognitoDomainPrefix_rejects_characters_cognito_disallows(string subdomain)
    {
        var act = () => ProvisioningNames.CognitoDomainPrefix("thor-dev", subdomain);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CognitoDomainPrefix_rejects_prefixes_over_63_characters()
    {
        var act = () => ProvisioningNames.CognitoDomainPrefix("thor-dev", new string('a', 55));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CognitoDomainHost_is_the_regional_amazoncognito_host() =>
        ProvisioningNames.CognitoDomainHost("thor-dev-acme", "us-east-1")
            .Should().Be("thor-dev-acme.auth.us-east-1.amazoncognito.com");
}
