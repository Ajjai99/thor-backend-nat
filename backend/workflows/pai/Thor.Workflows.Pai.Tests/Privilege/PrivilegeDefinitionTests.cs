using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.Graph;
using Thor.Workflows.Pai.Privilege;
using Thor.Workflows.Pai.Tests.Fixtures;
using Xunit;

namespace Thor.Workflows.Pai.Tests.Privilege;

public sealed class PrivilegePatternJsonTests
{
    [Fact]
    public void ShippedConfig_LoadsAndParses()
    {
        var definitions = new PrivilegeDefinitionProvider().GetAll();

        Assert.Equal([(short)308, (short)401, (short)402], definitions.Select(d => d.ConnectorType).Order());
        Assert.All(definitions, d => PrivilegePatternJson.Parse(d.PathPatternJson, d.Name, d.ConnectorType));
    }

    [Fact]
    public void AParsedPattern_CarriesItsConnector()
    {
        var pattern = PrivilegePatternJson.Parse(
            """{"edgeType":"MEMBER_OF","targetType":"grp","match":{"field":"displayName","operator":"equals","value":"Administrators"}}""", "t", 402);

        Assert.Equal(402, pattern.ConnectorType);
        Assert.True(pattern.IsTransitive);
    }

    [Theory]
    [InlineData("""{"edgeType":"MEMBER_OF","targetType":"grp","match":{"field":"display_name","operator":"equals","value":"x"}}""", "not a matchable property")]
    [InlineData("""{"edgeType":"MEMBER_OF","targetType":"grp","match":{"field":"displayName","operator":"like","value":"x"}}""", "unknown match.operator")]
    [InlineData("""{"edgeType":"MEMBER_OF","targetType":"account","match":{"field":"displayName","operator":"equals","value":"x"}}""", "unknown pathPattern.targetType")]
    [InlineData("""{"edgeType":"MEMBER_OF","targetType":"grp","match":{"field":"displayName","operator":"equals","values":["a","b"]}}""", "exactly one value")]
    [InlineData("""{"targetType":"grp","match":{"field":"displayName","operator":"equals","value":"x"}}""", "edgeType is required")]
    public void AMalformedPattern_IsRejectedRatherThanMatchingNothing(string json, string reason)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PrivilegePatternJson.Parse(json, "t", 308));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void APropertyKeyThatCouldInjectGremlin_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new PrivilegePattern("MEMBER_OF", "grp", "x').drop(", PrivilegeMatchOperator.Equals, ["v"]));
}

public sealed class PrivilegeDefinitionSeederTests(TenantDatabaseFixture db) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Seeding_InsertsThenUpdatesInPlace_AndDeactivatesWhatIsNoLongerShipped()
    {
        var first = new ConfiguredPrivilegeDefinition(308, "A", "d", "high", "{}", "hash-1");
        var dropped = new ConfiguredPrivilegeDefinition(308, "B", "d", "high", "{}", "hash-b");
        await SeedAsync([first, dropped]);
        var id = await IdOfAsync("A");

        await SeedAsync([first with { PathPatternJson = "{\"edited\":true}", DefinitionHash = "hash-2" }]);

        await using var context = db.NewContext();
        var rows = await context.PrivilegeDefinitions.AsNoTracking().ToDictionaryAsync(d => d.Name);
        Assert.Equal(id, rows["A"].Id);
        Assert.Equal("hash-2", rows["A"].DefinitionHash);
        Assert.True(rows["A"].IsActive);
        Assert.False(rows["B"].IsActive);
    }

    private async Task SeedAsync(IReadOnlyList<ConfiguredPrivilegeDefinition> configured)
    {
        await using var context = db.NewContext();
        await PrivilegeDefinitionSeeder.SeedAsync(context, configured, NullLogger.Instance, CancellationToken.None);
    }

    private async Task<Guid> IdOfAsync(string name)
    {
        await using var context = db.NewContext();
        return await context.PrivilegeDefinitions.Where(d => d.Name == name).Select(d => d.Id).SingleAsync();
    }
}
