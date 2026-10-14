using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Thor.Authorizer.Function.DataAccess;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Authorizer.Test.DataAccess;

public class MasterDbRefreshTokenRepositoryTests
{
    private static readonly MasterConnectionInfo ConnectionInfo =
        new(Host: "proxy.local", Database: "thor_master", Username: "thor_authorizer", Region: "us-east-1");

    [Fact]
    public async Task GetByTokenIdAsync_MapsTokenAndItsApiKeysTenant_ToRecord()
    {
        var databaseName = Guid.NewGuid().ToString();
        var (key, token) = await SeedAsync(databaseName);

        var record = await CreateRepository(databaseName).GetByTokenIdAsync(token.TokenId);

        record.Should().NotBeNull();
        record!.TokenId.Should().Be(token.TokenId);
        record.KeyId.Should().Be(key.KeyId);
        record.TenantId.Should().Be(key.TenantId);
        record.RoleType.Should().Be(token.RoleType);
        record.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetByTokenIdAsync_ReturnsNull_WhenTokenUnknown()
    {
        var databaseName = Guid.NewGuid().ToString();
        await SeedAsync(databaseName);

        var record = await CreateRepository(databaseName).GetByTokenIdAsync(Guid.NewGuid());

        record.Should().BeNull();
    }

    private static MasterDbRefreshTokenRepository CreateRepository(string databaseName) =>
        new(new InMemoryFactory(databaseName), ConnectionInfo);

    private static async Task<(TenantApiKey Key, TaskApiRefreshToken Token)> SeedAsync(string databaseName)
    {
        var key = new TenantApiKey
        {
            KeyId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SecretHash = "key-hash",
            Salt = "key-salt",
            StatusId = ApiKeyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };

        var token = new TaskApiRefreshToken
        {
            TokenId = Guid.NewGuid(),
            KeyId = key.KeyId,
            TokenHash = "token-hash",
            Salt = "token-salt",
            RoleType = "scanner",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };

        await using var context = new MasterDbContext(OptionsFor(databaseName));
        context.TenantApiKeys.Add(key);
        context.TaskApiRefreshTokens.Add(token);
        await context.SaveChangesAsync();

        return (key, token);
    }

    private static DbContextOptions<MasterDbContext> OptionsFor(string databaseName) =>
        new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

    // Same stand-in for MasterDbContextFactory as MasterDbTenantRoutingRepositoryTests.
    private sealed class InMemoryFactory(string databaseName) : IMasterDbContextFactory
    {
        public MasterDbContext Create(MasterConnectionInfo connectionInfo) =>
            new(OptionsFor(databaseName));
    }
}
