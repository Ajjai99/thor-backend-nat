using FluentAssertions;
using NSubstitute;
using Thor.Auth;
using Thor.Authorizer.Core.Auth.RefreshToken;
using Thor.Authorizer.Core.DataAccess;

namespace Thor.Authorizer.Test.Auth.RefreshToken;

public class RefreshTokenValidatorTests
{
    private readonly IRefreshTokenRepository _repository = Substitute.For<IRefreshTokenRepository>();

    private RefreshTokenValidator CreateSut() => new(_repository);

    private static (string RawToken, RefreshTokenRecord Record) IssueToken()
    {
        var record = new RefreshTokenRecord(
            TokenId: Guid.NewGuid(),
            KeyId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            RoleType: "scanner",
            RevokedAt: null);

        return (PrefixedSecretToken.Format(ThorTokenIssuer.RefreshTokenPrefix, record.TokenId, SecretHasher.GenerateSecret()), record);
    }

    [Fact]
    public async Task ValidateAsync_ExistingUnusedToken_ReturnsTenantKeyAndRoleFromRecord()
    {
        var (rawToken, record) = IssueToken();
        _repository.GetByTokenIdAsync(record.TokenId).Returns(record);

        var result = await CreateSut().ValidateAsync(rawToken);

        result.IsValid.Should().BeTrue();
        result.TenantId.Should().Be(record.TenantId.ToString());
        result.KeyId.Should().Be(record.KeyId.ToString());
        result.RoleType.Should().Be("scanner");
    }

    [Fact]
    public async Task ValidateAsync_DoesNotVerifySecret()
    {
        var (_, record) = IssueToken();
        _repository.GetByTokenIdAsync(record.TokenId).Returns(record);
        var anySecret = PrefixedSecretToken.Format(ThorTokenIssuer.RefreshTokenPrefix, record.TokenId, "any-secret");

        var result = await CreateSut().ValidateAsync(anySecret);

        result.IsValid.Should().BeTrue(); // Thor.Api verifies the secret, not the authorizer
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("thor_rt_not-a-guid_secret")]
    [InlineData("thor_00000000-0000-0000-0000-000000000000_secret")] // an API key, not a refresh token
    public async Task ValidateAsync_MalformedToken_FailsWithoutLookup(string rawToken)
    {
        var result = await CreateSut().ValidateAsync(rawToken);

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("malformed refresh token");
        await _repository.DidNotReceiveWithAnyArgs().GetByTokenIdAsync(default);
    }

    [Fact]
    public async Task ValidateAsync_UnknownToken_Fails()
    {
        var (rawToken, _) = IssueToken();

        var result = await CreateSut().ValidateAsync(rawToken);

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("refresh token not found");
    }

    [Fact]
    public async Task ValidateAsync_AlreadyUsedToken_Fails()
    {
        var (rawToken, record) = IssueToken();
        _repository.GetByTokenIdAsync(record.TokenId).Returns(record with { RevokedAt = DateTimeOffset.UtcNow });

        var result = await CreateSut().ValidateAsync(rawToken);

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("refresh token already used");
    }
}
