using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.Api.Test.TestFixtures;
using Thor.DataConnectionManager.Exceptions;
using Thor.DataConnectionManager.Routing;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Test.Services;

public class TenantBrandingServiceTests
{
    private const string Bucket = "thor-dev-tenant-assets-123";
    private const string ETag = "\"etag-1\"";

    private static readonly MasterConnectionInfo ConnectionInfo = new("localhost", "unused", "unused", "unused");
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UploadId = Guid.NewGuid();
    private static readonly string PendingKey = $"pending/{TenantId}/{UploadId}";

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly ITenantRoutingResolver _routingResolver = Substitute.For<ITenantRoutingResolver>();
    private readonly InMemoryMasterDbContextFactory _masterDb = new(Guid.NewGuid().ToString());

    public TenantBrandingServiceTests()
    {
        _routingResolver.ResolveAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantRouting { TenantId = TenantId, UserPoolId = "pool", AppClientId = "client" });
        _s3.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://s3.example.com/presigned");
    }

    private TenantBrandingService CreateService(IMasterDbContextFactory? masterDb = null) => new(
        _s3, masterDb ?? _masterDb, ConnectionInfo, _routingResolver, new TenantAssetsOptions(Bucket),
        TimeProvider.System, NullLogger<TenantBrandingService>.Instance);

    private void PendingUploadIs(long size, byte[] header)
    {
        _s3.GetObjectMetadataAsync(Bucket, PendingKey, Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse { ContentLength = size, ETag = ETag });
        _s3.GetObjectAsync(Arg.Is<GetObjectRequest>(r => r.BucketName == Bucket && r.Key == PendingKey), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse { ResponseStream = new MemoryStream(header) });
    }

    private void TenantHasLogo(string logoKey)
    {
        using var db = _masterDb.Create(ConnectionInfo);
        db.TenantBrandings.Add(new TenantBranding { TenantId = TenantId, LogoObjectKey = logoKey });
        db.SaveChanges();
    }

    private string? StoredLogoKey()
    {
        using var db = _masterDb.Create(ConnectionInfo);
        return db.TenantBrandings.SingleOrDefault(b => b.TenantId == TenantId)?.LogoObjectKey;
    }

    [Fact]
    public async Task CreateUpload_PresignsAPutBoundToTheTenantsPendingPrefix()
    {
        var response = await CreateService().CreateUploadAsync(TenantId, new CreateLogoUploadRequest("image/png"), CancellationToken.None);

        response.UploadUrl.Should().Be("https://s3.example.com/presigned");
        response.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
        await _s3.Received(1).GetPreSignedURLAsync(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.BucketName == Bucket &&
            r.Key == $"pending/{TenantId}/{response.UploadId}" &&
            r.Verb == HttpVerb.PUT &&
            r.ContentType == "image/png"));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("IMAGE/PNG")]
    public async Task CreateUpload_UnsupportedContentType_ThrowsWithoutPresigning(string contentType)
    {
        var act = () => CreateService().CreateUploadAsync(TenantId, new CreateLogoUploadRequest(contentType), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidLogoException>();
        _s3.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateUpload_UnknownTenant_FailsClosed()
    {
        var unknown = Guid.NewGuid();
        _routingResolver.ResolveAsync(unknown, Arg.Any<CancellationToken>()).ThrowsAsync(new TenantNotFoundException(unknown));

        var act = () => CreateService().CreateUploadAsync(unknown, new CreateLogoUploadRequest("image/png"), CancellationToken.None);

        await act.Should().ThrowAsync<TenantNotFoundException>();
        _s3.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_ValidPng_CopiesWithSniffedTypeRecordsItAndCleansUp()
    {
        PendingUploadIs(10_000, PngHeader);
        TenantHasLogo("tenant-assets/logos/old.jpg");

        var response = await CreateService().PublishAsync(TenantId, new PublishLogoRequest(UploadId), CancellationToken.None);

        response.LogoUrl.Should().MatchRegex("^/tenant-assets/logos/[0-9a-f]{32}\\.png$");
        StoredLogoKey().Should().Be(response.LogoUrl[1..]);
        await _s3.Received(1).CopyObjectAsync(Arg.Is<CopyObjectRequest>(r =>
                r.SourceBucket == Bucket && r.SourceKey == PendingKey &&
                r.DestinationBucket == Bucket && r.DestinationKey == response.LogoUrl.Substring(1) &&
                r.ETagToMatch == ETag &&
                r.MetadataDirective == S3MetadataDirective.REPLACE &&
                r.ContentType == "image/png"),
            Arg.Any<CancellationToken>());
        await _s3.Received(1).DeleteObjectAsync(Bucket, PendingKey, Arg.Any<CancellationToken>());
        await _s3.Received(1).DeleteObjectAsync(Bucket, "tenant-assets/logos/old.jpg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_MissingUpload_Throws()
    {
        _s3.GetObjectMetadataAsync(Bucket, PendingKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound });

        var act = () => CreateService().PublishAsync(TenantId, new PublishLogoRequest(UploadId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidLogoException>();
        await _s3.DidNotReceiveWithAnyArgs().CopyObjectAsync(default!, default);
    }

    [Fact]
    public async Task Publish_Oversized_ThrowsAndDeletesTheUpload()
    {
        PendingUploadIs(TenantBrandingService.MaxLogoBytes + 1, PngHeader);

        var act = () => CreateService().PublishAsync(TenantId, new PublishLogoRequest(UploadId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidLogoException>();
        await _s3.DidNotReceiveWithAnyArgs().CopyObjectAsync(default!, default);
        await _s3.Received(1).DeleteObjectAsync(Bucket, PendingKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_HtmlDisguisedAsAnImage_ThrowsAndDeletesTheUpload()
    {
        PendingUploadIs(100, "<html><script>"u8.ToArray());

        var act = () => CreateService().PublishAsync(TenantId, new PublishLogoRequest(UploadId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidLogoException>();
        await _s3.DidNotReceiveWithAnyArgs().CopyObjectAsync(default!, default);
        await _s3.Received(1).DeleteObjectAsync(Bucket, PendingKey, Arg.Any<CancellationToken>());
        StoredLogoKey().Should().BeNull();
    }

    [Fact]
    public async Task Publish_WhenRecordingFails_DeletesTheCopyAndKeepsTheUpload()
    {
        PendingUploadIs(10_000, PngHeader);
        var failingDb = Substitute.For<IMasterDbContextFactory>();
        failingDb.Create(Arg.Any<MasterConnectionInfo>()).Throws(new InvalidOperationException("db down"));
        CopyObjectRequest? copy = null;
        await _s3.CopyObjectAsync(Arg.Do<CopyObjectRequest>(r => copy = r), Arg.Any<CancellationToken>());

        var act = () => CreateService(failingDb).PublishAsync(TenantId, new PublishLogoRequest(UploadId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _s3.Received(1).DeleteObjectAsync(Bucket, copy!.DestinationKey, Arg.Any<CancellationToken>());
        await _s3.DidNotReceive().DeleteObjectAsync(Bucket, PendingKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_RemovesTheRowAndTheObject()
    {
        TenantHasLogo("tenant-assets/logos/current.png");

        await CreateService().DeleteAsync(TenantId, CancellationToken.None);

        StoredLogoKey().Should().BeNull();
        await _s3.Received(1).DeleteObjectAsync(Bucket, "tenant-assets/logos/current.png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NoLogo_ThrowsNotFound()
    {
        var act = () => CreateService().DeleteAsync(TenantId, CancellationToken.None);

        await act.Should().ThrowAsync<LogoNotFoundException>();
    }
}
