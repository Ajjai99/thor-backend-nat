using System.Text.Json;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.Api.Test.Support;
using Thor.Api.Test.TestFixtures;
using Thor.DataConnectionManager.Routing;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Test.Services;

public class IdentityProviderServiceTests
{
    private const string UserPoolId = "us-east-1_tenantPool";
    private const string AppClientId = "tenant-app-client";
    private const string OidcSecret = "super-secret-oidc-value";
    private const string Actor = "admin-sub-1";

    private static readonly MasterConnectionInfo ConnectionInfo = new("localhost", "unused", "unused", "unused");
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly IAmazonCognitoIdentityProvider _cognito = Substitute.For<IAmazonCognitoIdentityProvider>();
    private readonly ITenantRoutingResolver _routingResolver = Substitute.For<ITenantRoutingResolver>();
    private readonly InMemoryMasterDbContextFactory _masterDb = new(Guid.NewGuid().ToString());

    public IdentityProviderServiceTests()
    {
        _routingResolver.ResolveAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantRouting { TenantId = TenantId, UserPoolId = UserPoolId, AppClientId = AppClientId });
    }

    private IdentityProviderService CreateService(IMasterDbContextFactory? masterDb = null) =>
        new(_cognito, _routingResolver, masterDb ?? _masterDb, ConnectionInfo, TimeProvider.System,
            NullLogger<IdentityProviderService>.Instance);

    private void AppClientHasProviders(List<string>? providers) =>
        _cognito.DescribeUserPoolClientAsync(
                Arg.Is<DescribeUserPoolClientRequest>(r => r.UserPoolId == UserPoolId && r.ClientId == AppClientId),
                Arg.Any<CancellationToken>())
            .Returns(new DescribeUserPoolClientResponse
            {
                UserPoolClient = new UserPoolClientType
                {
                    UserPoolId = UserPoolId,
                    ClientId = AppClientId,
                    ClientName = "thor-acme-app",
                    SupportedIdentityProviders = providers,
                },
            });

    private void ProviderExists(
        string name, IdentityProviderTypeType type, Dictionary<string, string>? details = null,
        Dictionary<string, string>? attributeMapping = null) =>
        _cognito.DescribeIdentityProviderAsync(
                Arg.Is<DescribeIdentityProviderRequest>(r => r.UserPoolId == UserPoolId && r.ProviderName == name),
                Arg.Any<CancellationToken>())
            .Returns(new DescribeIdentityProviderResponse
            {
                IdentityProvider = new IdentityProviderType
                {
                    ProviderName = name, ProviderType = type, ProviderDetails = details, AttributeMapping = attributeMapping,
                },
            });

    private void ProviderDoesNotExist() =>
        _cognito.DescribeIdentityProviderAsync(Arg.Any<DescribeIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ResourceNotFoundException("missing"));

    private void CreateReturnsInput() =>
        _cognito.CreateIdentityProviderAsync(Arg.Any<CreateIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var r = ci.Arg<CreateIdentityProviderRequest>();
                return new CreateIdentityProviderResponse
                {
                    IdentityProvider = new IdentityProviderType
                    {
                        ProviderName = r.ProviderName,
                        ProviderType = r.ProviderType,
                        ProviderDetails = r.ProviderDetails,
                        AttributeMapping = r.AttributeMapping,
                    },
                };
            });

    private void UpdateReturnsInput(string name, IdentityProviderTypeType type) =>
        _cognito.UpdateIdentityProviderAsync(Arg.Any<UpdateIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new UpdateIdentityProviderResponse
            {
                IdentityProvider = new IdentityProviderType
                {
                    ProviderName = name,
                    ProviderType = type,
                    ProviderDetails = ci.Arg<UpdateIdentityProviderRequest>().ProviderDetails,
                    AttributeMapping = ci.Arg<UpdateIdentityProviderRequest>().AttributeMapping,
                },
            });

    private void SeedRecord(string name, bool enabled, string? vendor = null, string? samlSource = null)
    {
        using var db = _masterDb.Create(ConnectionInfo);
        db.TenantIdentityProviders.Add(new TenantIdentityProvider
        {
            TenantId = TenantId, ProviderName = name, Enabled = enabled, Vendor = vendor, SamlMetadataSource = samlSource,
            CreatedBySub = "creator", UpdatedBySub = "creator",
        });
        db.SaveChanges();
    }

    private TenantIdentityProvider? StoredRecord(string name)
    {
        using var db = _masterDb.Create(ConnectionInfo);
        return db.TenantIdentityProviders.SingleOrDefault(p => p.TenantId == TenantId && p.ProviderName == name);
    }

    private static IMasterDbContextFactory FailingMasterDb()
    {
        var failing = Substitute.For<IMasterDbContextFactory>();
        failing.Create(Arg.Any<MasterConnectionInfo>()).Throws(new InvalidOperationException("db down"));
        return failing;
    }

    private static CreateTenantIdentityProviderRequest SamlRequest(string name = "Okta") =>
        new(name, "SAML", new SamlProviderSettings("https://idp.example.com/metadata", null, null, true), null,
            new Dictionary<string, string> { ["email"] = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress" },
            null);

    private static CreateTenantIdentityProviderRequest OidcRequest(string name = "Entra") =>
        new(name, "OIDC", null,
            new OidcProviderSettings("oidc-client", OidcSecret, "https://login.example.com/v2.0", "openid email", null),
            null, null);

    private static SamlManualSettings ManualSaml(string? certificatePem = null) => new(
        "http://www.okta.com/exk123", "https://acme.okta.com/sso/saml", certificatePem ?? TestCertificates.ValidPem(), null);

    private static UpdateTenantIdentityProviderRequest OidcUpdate() =>
        new("OIDC", null,
            new OidcProviderSettings("oidc-client", OidcSecret, "https://login.example.com/v2.0", "openid email profile", "POST"),
            null, null);

    private static UpdateTenantIdentityProviderRequest SamlUpdate(bool? encryptedResponses = null, bool? signRequests = null) =>
        new("SAML", new SamlProviderSettings("https://idp.example.com/metadata", null, null, null, encryptedResponses, signRequests),
            null, null, null);

    // ── Create ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Saml_CreatesOnTenantPoolDisabled()
    {
        CreateReturnsInput();

        var response = await CreateService().CreateAsync(TenantId, SamlRequest() with { Vendor = "okta" }, Actor, CancellationToken.None);

        await _cognito.Received(1).CreateIdentityProviderAsync(
            Arg.Is<CreateIdentityProviderRequest>(r =>
                r.UserPoolId == UserPoolId &&
                r.ProviderName == "Okta" &&
                r.ProviderType == IdentityProviderTypeType.SAML &&
                r.ProviderDetails["MetadataURL"] == "https://idp.example.com/metadata" &&
                r.ProviderDetails["IDPSignout"] == "true"),
            Arg.Any<CancellationToken>());

        // Disabled: not on the app client, so nobody can sign in through it until an admin enables it.
        await _cognito.DidNotReceiveWithAnyArgs().UpdateUserPoolClientAsync(default!, default);
        StoredRecord("Okta").Should().BeEquivalentTo(new
        {
            Enabled = false, Vendor = "okta", SamlMetadataSource = "metadataUrl", CreatedBySub = Actor, UpdatedBySub = Actor,
        });

        response.ProviderName.Should().Be("Okta");
        response.Enabled.Should().BeFalse();
        response.Vendor.Should().Be("okta");
        response.Saml!.MetadataUrl.Should().Be("https://idp.example.com/metadata");
        response.Saml.MetadataSource.Should().Be("metadataUrl");
        response.Saml.IdpSignout.Should().BeTrue();
        response.Oidc.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_Saml_DefaultsToEncryptedResponsesAndSignedRequests()
    {
        CreateReturnsInput();

        var response = await CreateService().CreateAsync(TenantId, SamlRequest(), Actor, CancellationToken.None);

        await _cognito.Received(1).CreateIdentityProviderAsync(
            Arg.Is<CreateIdentityProviderRequest>(r =>
                r.ProviderDetails["EncryptedResponses"] == "true" &&
                r.ProviderDetails["RequestSigningAlgorithm"] == "rsa-sha256"),
            Arg.Any<CancellationToken>());
        response.Saml!.EncryptedResponses.Should().BeTrue();
        response.Saml.SignRequests.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_SamlWithEncryptionAndSigningOff_SendsThemOff()
    {
        CreateReturnsInput();
        var request = SamlRequest() with
        {
            Saml = new SamlProviderSettings("https://idp.example.com/metadata", null, null, null, EncryptedResponses: false, SignRequests: false),
        };

        await CreateService().CreateAsync(TenantId, request, Actor, CancellationToken.None);

        await _cognito.Received(1).CreateIdentityProviderAsync(
            Arg.Is<CreateIdentityProviderRequest>(r =>
                r.ProviderDetails["EncryptedResponses"] == "false" &&
                !r.ProviderDetails.ContainsKey("RequestSigningAlgorithm")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_Oidc_PassesSecretToCognitoButNeverReturnsIt()
    {
        CreateReturnsInput(); // Cognito echoes client_secret back in ProviderDetails

        var response = await CreateService().CreateAsync(TenantId, OidcRequest(), Actor, CancellationToken.None);

        await _cognito.Received(1).CreateIdentityProviderAsync(
            Arg.Is<CreateIdentityProviderRequest>(r =>
                r.ProviderType == IdentityProviderTypeType.OIDC &&
                r.ProviderDetails["client_secret"] == OidcSecret &&
                r.ProviderDetails["attributes_request_method"] == "GET"),
            Arg.Any<CancellationToken>());

        response.Oidc!.ClientId.Should().Be("oidc-client");
        StoredRecord("Entra")!.SamlMetadataSource.Should().BeNull();
        JsonSerializer.Serialize(response).Should().NotContain(OidcSecret);
    }

    [Fact]
    public async Task CreateAsync_WhenRecordingFails_DeletesTheNewProviderAndRethrows()
    {
        CreateReturnsInput();

        var act = () => CreateService(FailingMasterDb()).CreateAsync(TenantId, SamlRequest(), Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _cognito.Received(1).DeleteIdentityProviderAsync(
            Arg.Is<DeleteIdentityProviderRequest>(r => r.UserPoolId == UserPoolId && r.ProviderName == "Okta"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_StaleRecordUnderTheName_IsReplaced()
    {
        // e.g. the IdP was deleted in the Cognito console, leaving Thor's record behind
        SeedRecord("Okta", enabled: true, vendor: "entra");
        CreateReturnsInput();

        await CreateService().CreateAsync(TenantId, SamlRequest(), Actor, CancellationToken.None);

        StoredRecord("Okta").Should().BeEquivalentTo(new { Enabled = false, Vendor = (string?)null, CreatedBySub = Actor });
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ThrowsConflict()
    {
        _cognito.CreateIdentityProviderAsync(Arg.Any<CreateIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DuplicateProviderException("exists"));

        var act = () => CreateService().CreateAsync(TenantId, SamlRequest(), Actor, CancellationToken.None);

        await act.Should().ThrowAsync<IdentityProviderConflictException>();
        StoredRecord("Okta").Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_CognitoRejectsParameters_ThrowsInvalid()
    {
        _cognito.CreateIdentityProviderAsync(Arg.Any<CreateIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidParameterException("metadata unreachable"));

        var act = () => CreateService().CreateAsync(TenantId, SamlRequest(), Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>().WithMessage("metadata unreachable");
    }

    public static TheoryData<CreateTenantIdentityProviderRequest> InvalidCreateRequests() => new()
    {
        SamlRequest("COGNITO"),
        SamlRequest("cognito"),
        SamlRequest(" "),
        SamlRequest() with { ProviderType = "Google" },
        SamlRequest() with { Saml = null },
        SamlRequest() with { Saml = new SamlProviderSettings("https://idp/metadata", "<xml/>", null, null) },
        SamlRequest() with { Saml = new SamlProviderSettings(null, null, null, null) },
        SamlRequest() with { Saml = new SamlProviderSettings("https://idp/metadata", null, ManualSaml(), null) },
        SamlRequest() with { Saml = new SamlProviderSettings(null, null, ManualSaml() with { SsoUrl = "http://idp/sso" }, null) },
        SamlRequest() with { Saml = new SamlProviderSettings(null, null, ManualSaml(TestCertificates.ExpiredPem()), null) },
        OidcRequest() with { Oidc = OidcRequest().Oidc! with { ClientSecret = null } },
        SamlRequest() with { Oidc = OidcRequest().Oidc },
        OidcRequest() with { Oidc = OidcRequest().Oidc! with { ClientSecret = "" } },
        OidcRequest() with { Oidc = null },
        SamlRequest() with { Vendor = "Okta" }, // vendors are lower-case labels
        SamlRequest() with { Vendor = "onelogin" },
        // Only profile attributes can be mapped, so an IdP can't set verification flags or custom attributes.
        SamlRequest() with { AttributeMapping = new Dictionary<string, string> { ["email_verified"] = "verified" } },
        SamlRequest() with { AttributeMapping = new Dictionary<string, string> { ["custom:role"] = "groups" } },
        SamlRequest() with { AttributeMapping = new Dictionary<string, string> { ["phone_number"] = "phone" } },
    };

    [Theory]
    [MemberData(nameof(InvalidCreateRequests))]
    public async Task CreateAsync_InvalidRequest_ThrowsInvalidWithoutCallingCognito(CreateTenantIdentityProviderRequest request)
    {
        var act = () => CreateService().CreateAsync(TenantId, request, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>();
        _cognito.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_SamlManual_SendsBuiltMetadataAsMetadataFile()
    {
        CreateReturnsInput();
        using var certificate = TestCertificates.SelfSigned("CN=Acme Okta");
        var request = SamlRequest() with
        {
            Saml = new SamlProviderSettings(null, null, ManualSaml(certificate.ExportCertificatePem()), null),
        };

        var response = await CreateService().CreateAsync(TenantId, request, Actor, CancellationToken.None);

        await _cognito.Received(1).CreateIdentityProviderAsync(
            Arg.Is<CreateIdentityProviderRequest>(r =>
                !r.ProviderDetails.ContainsKey("MetadataURL") &&
                r.ProviderDetails["MetadataFile"].Contains("entityID=\"http://www.okta.com/exk123\"") &&
                r.ProviderDetails["MetadataFile"].Contains(Convert.ToBase64String(certificate.RawData))),
            Arg.Any<CancellationToken>());

        // The response is read back from the metadata Cognito holds, so the edit form can be pre-filled.
        response.Saml!.MetadataUrl.Should().BeNull();
        response.Saml.MetadataSource.Should().Be("manual");
        response.Saml.EntityId.Should().Be("http://www.okta.com/exk123");
        response.Saml.SsoUrl.Should().Be("https://acme.okta.com/sso/saml");
        response.Saml.SigningCertificates.Should().ContainSingle().Which.Subject.Should().Be("CN=Acme Okta");
    }

    [Fact]
    public async Task CreateAsync_SamlMetadataFile_RecordsFileSource()
    {
        CreateReturnsInput();
        var request = SamlRequest() with { Saml = new SamlProviderSettings(null, "<EntityDescriptor/>", null, null) };

        await CreateService().CreateAsync(TenantId, request, Actor, CancellationToken.None);

        // Cognito stores a file and a manual entry alike, so only Thor's record tells them apart.
        StoredRecord("Okta")!.SamlMetadataSource.Should().Be("metadataFile");
    }

    // ── Get / List ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_Oidc_NeverReturnsSecret()
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC, new Dictionary<string, string>
        {
            ["client_id"] = "oidc-client",
            ["client_secret"] = OidcSecret,
            ["oidc_issuer"] = "https://login.example.com/v2.0",
            ["authorize_scopes"] = "openid email",
            ["attributes_request_method"] = "GET",
        });

        var response = await CreateService().GetAsync(TenantId, "Entra", CancellationToken.None);

        response.Oidc!.Issuer.Should().Be("https://login.example.com/v2.0");
        JsonSerializer.Serialize(response).Should().NotContain(OidcSecret);
    }

    [Fact]
    public async Task GetAsync_ReturnsEnabledVendorAndSourceFromTheRecord()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string> { ["MetadataFile"] = "<x/>" });
        SeedRecord("Okta", enabled: true, vendor: "okta", samlSource: "metadataFile");

        var response = await CreateService().GetAsync(TenantId, "Okta", CancellationToken.None);

        response.Enabled.Should().BeTrue();
        response.Vendor.Should().Be("okta");
        response.Saml!.MetadataSource.Should().Be("metadataFile");
    }

    [Fact]
    public async Task GetAsync_ProviderWithoutRecord_IsReportedDisabled()
    {
        // An IdP created before Thor kept records — even one still listed on the app client.
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string> { ["MetadataURL"] = "https://idp/md" });

        var response = await CreateService().GetAsync(TenantId, "Okta", CancellationToken.None);

        response.Enabled.Should().BeFalse();
        response.Vendor.Should().BeNull();
        response.Saml!.MetadataSource.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_Saml_ReturnsEncryptionSettingsAndCertificate()
    {
        using var certificate = TestCertificates.SelfSigned("CN=Cognito encryption");
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string>
        {
            ["MetadataURL"] = "https://idp.example.com/metadata",
            ["EncryptedResponses"] = "true",
            ["RequestSigningAlgorithm"] = "rsa-sha256",
            ["ActiveEncryptionCertificate"] = certificate.ExportCertificatePem(),
        });

        var response = await CreateService().GetAsync(TenantId, "Okta", CancellationToken.None);

        response.Saml!.EncryptedResponses.Should().BeTrue();
        response.Saml.SignRequests.Should().BeTrue();
        response.Saml.EncryptionCertificate!.Subject.Should().Be("CN=Cognito encryption");
    }

    [Fact]
    public async Task GetAsync_SamlWithUnreadableMetadata_StillReturnsTheProvider()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string>
        {
            ["MetadataURL"] = "https://idp.example.com/metadata",
            ["MetadataFile"] = "<not xml",
            ["ActiveEncryptionCertificate"] = "not a certificate",
        });

        var response = await CreateService().GetAsync(TenantId, "Okta", CancellationToken.None);

        response.Saml!.MetadataUrl.Should().Be("https://idp.example.com/metadata");
        response.Saml.EntityId.Should().BeNull();
        response.Saml.SigningCertificates.Should().BeEmpty();
        response.Saml.EncryptionCertificate.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_FollowsPaginationAndReportsEnabledFromRecords()
    {
        _cognito.ListIdentityProvidersAsync(
                Arg.Is<ListIdentityProvidersRequest>(r => r.UserPoolId == UserPoolId && r.NextToken == null),
                Arg.Any<CancellationToken>())
            .Returns(new ListIdentityProvidersResponse
            {
                Providers = [new ProviderDescription { ProviderName = "Okta", ProviderType = IdentityProviderTypeType.SAML }],
                NextToken = "page-2",
            });
        _cognito.ListIdentityProvidersAsync(
                Arg.Is<ListIdentityProvidersRequest>(r => r.UserPoolId == UserPoolId && r.NextToken == "page-2"),
                Arg.Any<CancellationToken>())
            .Returns(new ListIdentityProvidersResponse
            {
                Providers = [new ProviderDescription { ProviderName = "Entra", ProviderType = IdentityProviderTypeType.OIDC }],
            });
        SeedRecord("Okta", enabled: true, vendor: "okta");

        var providers = await CreateService().ListAsync(TenantId, CancellationToken.None);

        providers.Select(p => (p.ProviderName, p.ProviderType, p.Enabled, p.Vendor))
            .Should().Equal(("Okta", "SAML", true, "okta"), ("Entra", "OIDC", false, null));
    }

    // ── Update ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ReplacesSettingsOnTenantPool()
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC);
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);

        await CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate(), Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r =>
                r.UserPoolId == UserPoolId &&
                r.ProviderName == "Entra" &&
                r.ProviderDetails["authorize_scopes"] == "openid email profile" &&
                r.ProviderDetails["attributes_request_method"] == "POST"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SendsTheFullMapping_SoARemovedRowIsRemoved()
    {
        // Cognito replaces the stored mapping with the one sent (verified on dev), so a row the
        // admin deleted is simply left out.
        ProviderExists("Entra", IdentityProviderTypeType.OIDC, attributeMapping: new Dictionary<string, string>
        {
            ["email"] = "email", ["given_name"] = "given_name", ["username"] = "sub",
        });
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);
        var request = OidcUpdate() with
        {
            AttributeMapping = new Dictionary<string, string> { ["email"] = "email", ["username"] = "sub" },
        };

        var response = await CreateService().UpdateAsync(TenantId, "Entra", request, Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r =>
                r.AttributeMapping.Count == 2 && !r.AttributeMapping.ContainsKey("given_name")),
            Arg.Any<CancellationToken>());
        response.AttributeMapping.Should().NotContainKey("given_name");
    }

    [Fact]
    public async Task UpdateAsync_UnchangedMappingOutsideTheAllowList_IsKept()
    {
        // Set before the allow-list existed; an edit form sends it back as loaded.
        ProviderExists("Entra", IdentityProviderTypeType.OIDC,
            attributeMapping: new Dictionary<string, string> { ["custom:legacy"] = "dept" });
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);
        var request = OidcUpdate() with
        {
            AttributeMapping = new Dictionary<string, string> { ["custom:legacy"] = "dept", ["email"] = "email" },
        };

        await CreateService().UpdateAsync(TenantId, "Entra", request, Actor, CancellationToken.None);

        await _cognito.ReceivedWithAnyArgs(1).UpdateIdentityProviderAsync(default!, default);
    }

    [Theory]
    [InlineData("custom:legacy", "other-claim")] // changed
    [InlineData("email_verified", "verified")] // added
    public async Task UpdateAsync_NewOrChangedMappingOutsideTheAllowList_ThrowsInvalid(string attribute, string claim)
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC,
            attributeMapping: new Dictionary<string, string> { ["custom:legacy"] = "dept" });
        var request = OidcUpdate() with { AttributeMapping = new Dictionary<string, string> { [attribute] = claim } };

        var act = () => CreateService().UpdateAsync(TenantId, "Entra", request, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>();
        await _cognito.DidNotReceiveWithAnyArgs().UpdateIdentityProviderAsync(default!, default);
    }

    [Fact]
    public async Task UpdateAsync_SamlOmittingEncryptionAndSigning_KeepsTheCurrentSettings()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string>
        {
            ["MetadataURL"] = "https://idp.example.com/metadata",
            ["EncryptedResponses"] = "true",
            ["RequestSigningAlgorithm"] = "rsa-sha256",
        });
        UpdateReturnsInput("Okta", IdentityProviderTypeType.SAML);

        await CreateService().UpdateAsync(TenantId, "Okta", SamlUpdate(), Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r =>
                r.ProviderDetails["EncryptedResponses"] == "true" &&
                r.ProviderDetails["RequestSigningAlgorithm"] == "rsa-sha256"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SamlCreatedWithoutEncryption_StaysUnencryptedUnlessAsked()
    {
        // Turning encryption on for a working IdP would break sign-in until the IdP side is configured.
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string> { ["MetadataURL"] = "https://idp/md" });
        UpdateReturnsInput("Okta", IdentityProviderTypeType.SAML);

        await CreateService().UpdateAsync(TenantId, "Okta", SamlUpdate(), Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r =>
                r.ProviderDetails["EncryptedResponses"] == "false" &&
                !r.ProviderDetails.ContainsKey("RequestSigningAlgorithm")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SamlTurningEncryptionOn_SendsIt()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string> { ["MetadataURL"] = "https://idp/md" });
        UpdateReturnsInput("Okta", IdentityProviderTypeType.SAML);

        await CreateService().UpdateAsync(TenantId, "Okta", SamlUpdate(encryptedResponses: true, signRequests: true), Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r =>
                r.ProviderDetails["EncryptedResponses"] == "true" &&
                r.ProviderDetails["RequestSigningAlgorithm"] == "rsa-sha256"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_UpdatesTheRecordButNotItsEnabledFlag()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML, new Dictionary<string, string> { ["MetadataURL"] = "https://idp/md" });
        UpdateReturnsInput("Okta", IdentityProviderTypeType.SAML);
        SeedRecord("Okta", enabled: true, vendor: "okta", samlSource: "manual");
        var request = SamlUpdate() with { Vendor = "adfs" };

        var response = await CreateService().UpdateAsync(TenantId, "Okta", request, Actor, CancellationToken.None);

        StoredRecord("Okta").Should().BeEquivalentTo(new
        {
            Enabled = true, Vendor = "adfs", SamlMetadataSource = "metadataUrl", CreatedBySub = "creator", UpdatedBySub = Actor,
        });
        response.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_OmittedVendor_KeepsTheCurrentOne()
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC);
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);
        SeedRecord("Entra", enabled: false, vendor: "entra");

        await CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate(), Actor, CancellationToken.None);

        StoredRecord("Entra")!.Vendor.Should().Be("entra");
    }

    [Fact]
    public async Task UpdateAsync_ProviderWithoutRecord_GetsADisabledOne()
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC);
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);

        await CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate() with { Vendor = "entra" }, Actor, CancellationToken.None);

        StoredRecord("Entra").Should().BeEquivalentTo(new { Enabled = false, Vendor = "entra", CreatedBySub = Actor });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpdateAsync_OidcWithBlankSecret_KeepsTheSecretCognitoHolds(string? blankSecret)
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC, new Dictionary<string, string>
        {
            ["client_id"] = "oidc-client",
            ["client_secret"] = OidcSecret,
        });
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);
        var request = OidcUpdate() with { Oidc = OidcUpdate().Oidc! with { ClientSecret = blankSecret } };

        var response = await CreateService().UpdateAsync(TenantId, "Entra", request, Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r => r.ProviderDetails["client_secret"] == OidcSecret),
            Arg.Any<CancellationToken>());
        JsonSerializer.Serialize(response).Should().NotContain(OidcSecret);
    }

    [Fact]
    public async Task UpdateAsync_OidcWithNewSecret_ReplacesIt()
    {
        ProviderExists("Entra", IdentityProviderTypeType.OIDC, new Dictionary<string, string> { ["client_secret"] = "old" });
        UpdateReturnsInput("Entra", IdentityProviderTypeType.OIDC);

        await CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate(), Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateIdentityProviderAsync(
            Arg.Is<UpdateIdentityProviderRequest>(r => r.ProviderDetails["client_secret"] == OidcSecret),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_ChangingProviderType_ThrowsInvalid()
    {
        ProviderExists("Entra", IdentityProviderTypeType.SAML);

        var act = () => CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate(), Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>();
        await _cognito.DidNotReceiveWithAnyArgs().UpdateIdentityProviderAsync(default!, default);
    }

    [Fact]
    public async Task UpdateAsync_UnknownVendor_ThrowsInvalidWithoutCallingCognito()
    {
        var act = () => CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate() with { Vendor = "onelogin" }, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>();
        _cognito.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_UnknownProvider_ThrowsNotFound()
    {
        ProviderDoesNotExist();

        var act = () => CreateService().UpdateAsync(TenantId, "Entra", OidcUpdate(), Actor, CancellationToken.None);

        await act.Should().ThrowAsync<IdentityProviderNotFoundException>();
    }

    // ── Enable / disable ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetEnabledAsync_Enable_AddsToAppClientAndRecordsIt()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO", "Entra"]);
        SeedRecord("Okta", enabled: false, vendor: "okta");

        await CreateService().SetEnabledAsync(TenantId, "Okta", enabled: true, Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateUserPoolClientAsync(
            Arg.Is<UpdateUserPoolClientRequest>(r =>
                r.UserPoolId == UserPoolId &&
                r.ClientId == AppClientId &&
                r.ClientName == "thor-acme-app" &&
                r.SupportedIdentityProviders.SequenceEqual(new[] { "COGNITO", "Entra", "Okta" })),
            Arg.Any<CancellationToken>());
        StoredRecord("Okta").Should().BeEquivalentTo(new { Enabled = true, Vendor = "okta", UpdatedBySub = Actor });
    }

    [Fact]
    public async Task SetEnabledAsync_EnableWithNoProvidersListed_AlwaysKeepsCognito()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(null); // how tenant provisioning creates the app client today

        await CreateService().SetEnabledAsync(TenantId, "Okta", enabled: true, Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateUserPoolClientAsync(
            Arg.Is<UpdateUserPoolClientRequest>(r => r.SupportedIdentityProviders.SequenceEqual(new[] { "COGNITO", "Okta" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetEnabledAsync_EnableWhenAppClientUpdateFails_RecordStaysDisabled()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO"]);
        SeedRecord("Okta", enabled: false);
        _cognito.UpdateUserPoolClientAsync(Arg.Any<UpdateUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TooManyRequestsException("throttled"));

        var act = () => CreateService().SetEnabledAsync(TenantId, "Okta", enabled: true, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        StoredRecord("Okta")!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabledAsync_Disable_RecordsItAndRemovesFromAppClient()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO", "Okta", "Entra"]);
        SeedRecord("Okta", enabled: true);

        await CreateService().SetEnabledAsync(TenantId, "Okta", enabled: false, Actor, CancellationToken.None);

        await _cognito.Received(1).UpdateUserPoolClientAsync(
            Arg.Is<UpdateUserPoolClientRequest>(r => r.SupportedIdentityProviders.SequenceEqual(new[] { "COGNITO", "Entra" })),
            Arg.Any<CancellationToken>());
        StoredRecord("Okta")!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabledAsync_DisableWhenAppClientUpdateFails_RecordIsAlreadyDisabled()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO", "Okta"]);
        SeedRecord("Okta", enabled: true);
        _cognito.UpdateUserPoolClientAsync(Arg.Any<UpdateUserPoolClientRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TooManyRequestsException("throttled"));

        var act = () => CreateService().SetEnabledAsync(TenantId, "Okta", enabled: false, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        StoredRecord("Okta")!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabledAsync_DisableProviderWithoutRecord_RecordsItDisabled()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO", "Okta"]);

        await CreateService().SetEnabledAsync(TenantId, "Okta", enabled: false, Actor, CancellationToken.None);

        StoredRecord("Okta").Should().BeEquivalentTo(new { Enabled = false, CreatedBySub = Actor });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetEnabledAsync_UnknownProvider_ThrowsNotFoundWithoutChangingAnything(bool enabled)
    {
        ProviderDoesNotExist();

        var act = () => CreateService().SetEnabledAsync(TenantId, "Okta", enabled, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<IdentityProviderNotFoundException>();
        await _cognito.DidNotReceiveWithAnyArgs().UpdateUserPoolClientAsync(default!, default);
        StoredRecord("Okta").Should().BeNull();
    }

    [Theory]
    [InlineData("COGNITO")]
    [InlineData("Cognito")]
    public async Task SetEnabledAsync_CognitoProvider_IsRejected(string providerName)
    {
        var act = () => CreateService().SetEnabledAsync(TenantId, providerName, enabled: false, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>();
        _cognito.ReceivedCalls().Should().BeEmpty();
    }

    // ── Delete ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesFromAppClientKeepingOthersThenDeletesProviderAndRecord()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO", "Okta", "Entra"]);
        SeedRecord("Okta", enabled: true);

        await CreateService().DeleteAsync(TenantId, "Okta", Actor, CancellationToken.None);

        Received.InOrder(() =>
        {
            _cognito.UpdateUserPoolClientAsync(
                Arg.Is<UpdateUserPoolClientRequest>(r => r.SupportedIdentityProviders.SequenceEqual(new[] { "COGNITO", "Entra" })),
                Arg.Any<CancellationToken>());
            _cognito.DeleteIdentityProviderAsync(
                Arg.Is<DeleteIdentityProviderRequest>(r => r.UserPoolId == UserPoolId && r.ProviderName == "Okta"),
                Arg.Any<CancellationToken>());
        });
        StoredRecord("Okta").Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenCognitoDeleteFails_LeavesADisabledProviderToRetry()
    {
        ProviderExists("Okta", IdentityProviderTypeType.SAML);
        AppClientHasProviders(["COGNITO", "Okta"]);
        SeedRecord("Okta", enabled: true);
        _cognito.DeleteIdentityProviderAsync(Arg.Any<DeleteIdentityProviderRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TooManyRequestsException("throttled"));

        var act = () => CreateService().DeleteAsync(TenantId, "Okta", Actor, CancellationToken.None);

        // Off the app client and without a record: disabled.
        await act.Should().ThrowAsync<TooManyRequestsException>();
        await _cognito.ReceivedWithAnyArgs(1).UpdateUserPoolClientAsync(default!, default);
        StoredRecord("Okta").Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownProvider_ThrowsNotFoundWithoutTouchingAppClient()
    {
        ProviderDoesNotExist();
        SeedRecord("Okta", enabled: true);

        var act = () => CreateService().DeleteAsync(TenantId, "Okta", Actor, CancellationToken.None);

        await act.Should().ThrowAsync<IdentityProviderNotFoundException>();
        await _cognito.DidNotReceiveWithAnyArgs().UpdateUserPoolClientAsync(default!, default);
        await _cognito.DidNotReceiveWithAnyArgs().DeleteIdentityProviderAsync(default!, default);
        StoredRecord("Okta").Should().NotBeNull();
    }

    [Theory]
    [InlineData("COGNITO")]
    [InlineData("Cognito")]
    public async Task DeleteAsync_CognitoProvider_IsRejected(string providerName)
    {
        var act = () => CreateService().DeleteAsync(TenantId, providerName, Actor, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidIdentityProviderException>();
        await _cognito.DidNotReceiveWithAnyArgs().UpdateUserPoolClientAsync(default!, default);
        await _cognito.DidNotReceiveWithAnyArgs().DeleteIdentityProviderAsync(default!, default);
    }
}
