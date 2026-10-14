using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Thor.Api.Constants;
using Thor.Api.Controllers.V1;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;
using Thor.DataLayer.Models.Tenants;

namespace Thor.Api.Test.Controllers.V1;

public class AuthenticationMethodsControllerTests
{
    private const short ConnectorTypeId = 1;

    private readonly string _masterDbName = $"master-{Guid.NewGuid()}";

    private MasterDbContext CreateMasterDbContext() =>
        new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(_masterDbName).Options);

    private void SeedMasterDb(Action<MasterDbContext> seed)
    {
        using var context = CreateMasterDbContext();
        seed(context);
        context.SaveChanges();
    }

    private Guid SeedAuthenticationType(string name, params short[] connectorTypes)
    {
        var id = Guid.NewGuid();
        SeedMasterDb(db =>
        {
            db.AuthenticationTypes.Add(new AuthenticationType { Id = id, Name = name });
            db.AuthenticationTypeConnectorTypes.AddRange(connectorTypes.Select(c =>
                new AuthenticationTypeConnectorType { AuthenticationTypeId = id, ConnectorTypeId = c }));
        });
        return id;
    }

    private readonly string _tenantDbName = $"tenant-{Guid.NewGuid()}";

    private readonly IAuthenticationSecretWriter _secretWriter = Substitute.For<IAuthenticationSecretWriter>();

    private TenantDbContext CreateTenantDbContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(_tenantDbName).Options);

    private AuthenticationMethodsController CreateController()
    {
        var masterFactory = Substitute.For<IMasterDbContextFactory>();
        masterFactory.Create(Arg.Any<MasterConnectionInfo>()).Returns(_ => CreateMasterDbContext());

        var tenantConnectionManager = Substitute.For<ITenantConnectionManager>();
        tenantConnectionManager.GetTenantDbContextAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(CreateTenantDbContext()));

        var masterConnectionInfo = new MasterConnectionInfo("host", "db", "user", "password");
        var service = new AuthenticationMethodService(
            tenantConnectionManager,
            masterFactory,
            masterConnectionInfo,
            _secretWriter,
            NullLogger<AuthenticationMethodService>.Instance);

        return new AuthenticationMethodsController(service, NullLogger<AuthenticationMethodsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // Sets the trusted tenant/actor headers, and the multipart form carrying any file parts.
    private static void SetRequest(AuthenticationMethodsController controller, params IFormFile[] files)
    {
        var request = controller.Request;
        request.Headers[TenantConstants.TenantHeaderName] = Guid.NewGuid().ToString();
        request.Headers[TenantConstants.ActorHeaderName] = "actor-1";
        request.ContentType = "multipart/form-data; boundary=test";
        var fileCollection = new FormFileCollection();
        fileCollection.AddRange(files);
        request.Form = new FormCollection(new Dictionary<string, StringValues>(), fileCollection);
    }

    private static IFormFile CreateFile(string partName, int length, string fileName = "id_rsa.pem") =>
        new FormFile(new MemoryStream(new byte[length]), 0, length, partName, fileName);

    private static string RequestJson(Guid typeId, params (Guid FieldId, string Value)[] values) =>
        JsonSerializer.Serialize(new
        {
            typeId,
            name = "Prod SSH",
            values = values.Select(v => new { fieldId = v.FieldId, value = v.Value }),
        });

    // Seeds an "SSH Key" type with a plain username field and a file-family private-key field.
    private (Guid TypeId, Guid UsernameId, Guid KeyFileId) SeedSshKeyType()
    {
        var typeId = SeedAuthenticationType("SSH Key");
        var usernameId = Guid.NewGuid();
        var keyFileId = Guid.NewGuid();
        SeedMasterDb(db => db.AuthenticationFields.AddRange(
            new AuthenticationField { Id = usernameId, TypeId = typeId, Name = "Username", DisplayName = "Username", InputType = "string" },
            new AuthenticationField { Id = keyFileId, TypeId = typeId, Name = "Private Key", DisplayName = "Private Key", InputType = "file" }));
        return (typeId, usernameId, keyFileId);
    }

    private Guid SeedAuthenticationMethod(Guid typeId, params (Guid ValueId, Guid FieldId, string? FileName)[] values)
    {
        var methodId = Guid.NewGuid();
        using var db = CreateTenantDbContext();
        db.AuthenticationMethods.Add(new AuthenticationMethod
        {
            Id = methodId,
            TypeId = typeId,
            Name = "Prod",
            AuthenticationValues = values.Select(v => new AuthenticationValue
            {
                Id = v.ValueId,
                MethodId = methodId,
                FieldId = v.FieldId,
                FileName = v.FileName,
                SecretArn = "arn",
                CreatedBy = "seed",
                UpdatedBy = "seed",
            }).ToList(),
        });
        db.SaveChanges();
        return methodId;
    }

    /// <summary>Returns every authentication type in the Master metadata DB, ordered by name.</summary>
    [Fact]
    public async Task ListTypes_ReturnsAllAuthenticationTypesOrderedByName()
    {
        SeedMasterDb(db => db.ConnectorTypes.Add(new ConnectorType { Id = ConnectorTypeId, Name = "Active Directory" }));
        var oauthId = SeedAuthenticationType("OAuth", ConnectorTypeId);
        var apiKeyId = SeedAuthenticationType("API Key");
        var controller = CreateController();

        var result = await controller.ListTypes(null, CancellationToken.None);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AuthenticationTypeResponse>>().Subject;
        response.Should().Equal(
            new AuthenticationTypeResponse(apiKeyId, "API Key"),
            new AuthenticationTypeResponse(oauthId, "OAuth"));
    }

    /// <summary>With ?connectorType=, only the types mapped to that connector type are returned.</summary>
    [Fact]
    public async Task ListTypes_WithConnectorType_ReturnsOnlyMappedTypes()
    {
        const short otherConnectorTypeId = ConnectorTypeId + 1;
        SeedMasterDb(db => db.ConnectorTypes.AddRange(
            new ConnectorType { Id = ConnectorTypeId, Name = "Active Directory" },
            new ConnectorType { Id = otherConnectorTypeId, Name = "SailPoint" }));
        var passwordId = SeedAuthenticationType("Local Username and Password", ConnectorTypeId, otherConnectorTypeId);
        var ldapId = SeedAuthenticationType("LDAP Authentication", ConnectorTypeId);
        SeedAuthenticationType("API Key", otherConnectorTypeId);
        SeedAuthenticationType("SSH Key");
        var controller = CreateController();

        var result = await controller.ListTypes(ConnectorTypeId, CancellationToken.None);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AuthenticationTypeResponse>>().Subject;
        response.Should().Equal(
            new AuthenticationTypeResponse(ldapId, "LDAP Authentication"),
            new AuthenticationTypeResponse(passwordId, "Local Username and Password"));
    }

    /// <summary>A connector type with no mapped auth types returns an empty list, not an error.</summary>
    [Fact]
    public async Task ListTypes_WithConnectorTypeWithNoMappings_ReturnsEmptyList()
    {
        SeedAuthenticationType("API Key");
        var controller = CreateController();

        var result = await controller.ListTypes(ConnectorTypeId, CancellationToken.None);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AuthenticationTypeResponse>>().Subject;
        response.Should().BeEmpty();
    }

    /// <summary>An empty Master DB returns an empty list, not an error.</summary>
    [Fact]
    public async Task ListTypes_NoAuthenticationTypes_ReturnsEmptyList()
    {
        var controller = CreateController();

        var result = await controller.ListTypes(null, CancellationToken.None);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AuthenticationTypeResponse>>().Subject;
        response.Should().BeEmpty();
    }

    /// <summary>Returns only the given type's fields, with their details, ordered by display order.</summary>
    [Fact]
    public async Task ListFields_ReturnsFieldsForTypeOrderedByDisplayOrder()
    {
        var typeId = SeedAuthenticationType("Local Username and Password");
        var otherTypeId = SeedAuthenticationType("API Key");
        var usernameId = Guid.NewGuid();
        var passwordId = Guid.NewGuid();
        SeedMasterDb(db => db.AuthenticationFields.AddRange(
            new AuthenticationField { Id = usernameId, TypeId = typeId, Name = "username", DisplayName = "Username", InputType = "text", Description = "Login name", DisplayOrder = 1 },
            new AuthenticationField { Id = passwordId, TypeId = typeId, Name = "password", DisplayName = "Password", InputType = "password", DisplayOrder = 2 },
            new AuthenticationField { Id = Guid.NewGuid(), TypeId = otherTypeId, Name = "api_key", DisplayName = "API Key", InputType = "password" }));
        var controller = CreateController();

        var result = await controller.ListFields(typeId, CancellationToken.None);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AuthenticationFieldResponse>>().Subject;
        response.Should().Equal(
            new AuthenticationFieldResponse(usernameId, "username", "Username", "text", "Login name"),
            new AuthenticationFieldResponse(passwordId, "password", "Password", "password", null));
    }

    /// <summary>A known type with no fields returns an empty list, not an error.</summary>
    [Fact]
    public async Task ListFields_TypeWithNoFields_ReturnsEmptyList()
    {
        var typeId = SeedAuthenticationType("API Key");
        var controller = CreateController();

        var result = await controller.ListFields(typeId, CancellationToken.None);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeAssignableTo<IReadOnlyList<AuthenticationFieldResponse>>().Subject;
        response.Should().BeEmpty();
    }

    /// <summary>An unknown type returns 404.</summary>
    [Fact]
    public async Task ListFields_UnknownType_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.ListFields(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>A `request` form field that isn't valid JSON returns 400.</summary>
    [Fact]
    public async Task Create_InvalidRequestJson_ReturnsBadRequest()
    {
        var controller = CreateController();
        SetRequest(controller);

        var result = await controller.Create("{not json", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    /// <summary>A request JSON without `values` returns 400 rather than throwing.</summary>
    [Fact]
    public async Task Create_RequestJsonWithoutValues_ReturnsBadRequest()
    {
        var controller = CreateController();
        SetRequest(controller);

        var result = await controller.Create("""{"typeId":"00000000-0000-0000-0000-000000000001","name":"x"}""", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    /// <summary>A file part whose name isn't a field id returns 400.</summary>
    [Fact]
    public async Task Create_FilePartNotNamedByFieldId_ReturnsBadRequest()
    {
        var (typeId, usernameId, keyFileId) = SeedSshKeyType();
        var controller = CreateController();
        SetRequest(controller, CreateFile("privateKey", 10));

        var result = await controller.Create(RequestJson(typeId, (usernameId, "alice"), (keyFileId, "")), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<string>().Which.Should().Contain("privateKey");
    }

    /// <summary>A file over the 16 KB cap returns 400 before anything is stored.</summary>
    [Fact]
    public async Task Create_FileOverSizeLimit_ReturnsBadRequest()
    {
        var (typeId, usernameId, keyFileId) = SeedSshKeyType();
        var controller = CreateController();
        SetRequest(controller, CreateFile(keyFileId.ToString(), 16 * 1024 + 1));

        var result = await controller.Create(RequestJson(typeId, (usernameId, "alice"), (keyFileId, "")), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<string>().Which.Should().Contain("16 KB");
        await _secretWriter.DidNotReceiveWithAnyArgs().StoreValuesAsync(default, default, default!, default);
    }

    /// <summary>A file uploaded for a field whose input type isn't file-family returns 400.</summary>
    [Fact]
    public async Task Create_FileForNonFileField_ReturnsBadRequest()
    {
        var (typeId, usernameId, keyFileId) = SeedSshKeyType();
        var controller = CreateController();
        SetRequest(controller, CreateFile(usernameId.ToString(), 10));

        var result = await controller.Create(RequestJson(typeId, (usernameId, ""), (keyFileId, "")), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<string>().Which.Should().Contain(usernameId.ToString());
        await _secretWriter.DidNotReceiveWithAnyArgs().StoreValuesAsync(default, default, default!, default);
    }

    /// <summary>
    /// Plain values come back as stored, password values are masked, and file values show the
    /// stored filename, never the file content.
    /// </summary>
    [Fact]
    public async Task GetValues_ReturnsPlainValuesAndRedactsPasswordsAndFiles()
    {
        var typeId = SeedAuthenticationType("OAuth 2.0 Client Credentials");
        var clientId = Guid.NewGuid();
        var secretId = Guid.NewGuid();
        var certId = Guid.NewGuid();
        var certPasswordId = Guid.NewGuid();
        SeedMasterDb(db => db.AuthenticationFields.AddRange(
            new AuthenticationField { Id = clientId, TypeId = typeId, Name = "Client ID", DisplayName = "Client ID", InputType = "string" },
            new AuthenticationField { Id = secretId, TypeId = typeId, Name = "Client Secret", DisplayName = "Client Secret", InputType = "radio,password" },
            new AuthenticationField { Id = certId, TypeId = typeId, Name = "Certificate", DisplayName = "Certificate", InputType = "radio,file" },
            new AuthenticationField { Id = certPasswordId, TypeId = typeId, Name = "Certificate Password", DisplayName = "Certificate Password", InputType = "certificate-password" }));
        var (clientValueId, secretValueId, certValueId, certPasswordValueId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var methodId = SeedAuthenticationMethod(typeId,
            (clientValueId, clientId, null),
            (secretValueId, secretId, null),
            (certValueId, certId, "client.pfx"),
            (certPasswordValueId, certPasswordId, null));
        _secretWriter.GetValuesAsync(Arg.Any<Guid>(), typeId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
            {
                [clientValueId] = "my-client",
                [secretValueId] = "real-secret",
                [certValueId] = "QkFTRTY0",
                [certPasswordValueId] = "",
            });
        var controller = CreateController();
        SetRequest(controller);

        var result = await controller.GetValues(methodId, CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AuthenticationMethodValuesResponse>().Subject;
        response.TypeId.Should().Be(typeId);
        response.Values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Client ID"] = "my-client",
            ["Client Secret"] = "********",
            ["Certificate"] = "client.pfx",
        });
    }

    /// <summary>An unknown method id returns 404.</summary>
    [Fact]
    public async Task GetValues_UnknownMethod_ReturnsNotFound()
    {
        var controller = CreateController();
        SetRequest(controller);

        var result = await controller.GetValues(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>A missing tenant header returns 400.</summary>
    [Fact]
    public async Task GetValues_MissingTenantHeader_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.GetValues(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    /// <summary>Updating an unknown method id returns 404.</summary>
    [Fact]
    public async Task Update_UnknownMethod_ReturnsNotFound()
    {
        var (typeId, usernameId, keyFileId) = SeedSshKeyType();
        var controller = CreateController();
        SetRequest(controller);

        var result = await controller.Update(Guid.NewGuid(), RequestJson(typeId, (usernameId, "alice"), (keyFileId, "")), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>Values that don't match the target type's fields return 400 and store nothing.</summary>
    [Fact]
    public async Task Update_MissingField_ReturnsBadRequest()
    {
        var (typeId, usernameId, keyFileId) = SeedSshKeyType();
        var methodId = SeedAuthenticationMethod(typeId, (Guid.NewGuid(), usernameId, null), (Guid.NewGuid(), keyFileId, "id_rsa.pem"));
        var controller = CreateController();
        SetRequest(controller);

        var result = await controller.Update(methodId, RequestJson(typeId, (usernameId, "bob")), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        await _secretWriter.DidNotReceiveWithAnyArgs().StoreValuesAsync(default, default, default!, default);
    }

    /// <summary>A file uploaded for a non-file field on update returns 400 and stores nothing.</summary>
    [Fact]
    public async Task Update_FileForNonFileField_ReturnsBadRequest()
    {
        var (typeId, usernameId, keyFileId) = SeedSshKeyType();
        var methodId = SeedAuthenticationMethod(typeId, (Guid.NewGuid(), usernameId, null), (Guid.NewGuid(), keyFileId, "id_rsa.pem"));
        var controller = CreateController();
        SetRequest(controller, CreateFile(usernameId.ToString(), 10));

        var result = await controller.Update(methodId, RequestJson(typeId, (usernameId, ""), (keyFileId, "")), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        await _secretWriter.DidNotReceiveWithAnyArgs().StoreValuesAsync(default, default, default!, default);
    }
}
