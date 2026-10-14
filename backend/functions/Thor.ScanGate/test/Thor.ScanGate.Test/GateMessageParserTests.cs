using System.Text.Json;
using Thor.ScanGate.Function;

namespace Thor.ScanGate.Test;

public class GateMessageParserTests
{
    private static string StatusChangeEvent(object input) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["source"] = "aws.states",
        ["detail-type"] = "Step Functions Execution Status Change",
        ["detail"] = new Dictionary<string, object>
        {
            ["status"] = "SUCCEEDED",
            ["input"] = JsonSerializer.Serialize(input),
        },
    });

    [Fact]
    public void IngestionExecutionEvent_CarriesTheScanAndTheManifest()
    {
        var (tenantId, scanId, manifestId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var message = GateMessageParser.TryParse("m1", StatusChangeEvent(new
        {
            TenantId = tenantId, ExportLocation = "s3://bucket", ScanId = scanId, ScanManifestId = manifestId, BatchSeq = 0, RunId = (Guid?)null,
        }));

        Assert.NotNull(message);
        Assert.Equal(("m1", tenantId, scanId, manifestId), (message.MessageId, message.TenantId, message.ScanId, message.ScanManifestId));
    }

    [Fact]
    public void AtreOrOwnershipExecutionEvent_CarriesOnlyTheManifest()
    {
        var (tenantId, manifestId) = (Guid.NewGuid(), Guid.NewGuid());

        var message = GateMessageParser.TryParse("m1", StatusChangeEvent(new { TenantId = tenantId, ScanManifestId = manifestId }));

        Assert.NotNull(message);
        Assert.Null(message.ScanId);
        Assert.Equal(manifestId, message.ScanManifestId);
    }

    [Fact]
    public void DirectMessage_CarriesTheScan()
    {
        var (tenantId, scanId) = (Guid.NewGuid(), Guid.NewGuid());

        var message = GateMessageParser.TryParse("m1", JsonSerializer.Serialize(new { TenantId = tenantId, ScanId = scanId }));

        Assert.NotNull(message);
        Assert.Equal((tenantId, scanId), (message.TenantId, message.ScanId));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"ScanId\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\"}")]
    [InlineData("{\"TenantId\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\"}")]
    [InlineData("{\"detail\":{\"status\":\"SUCCEEDED\"}}")]
    public void UnrecognisedBody_IsRejected(string body) =>
        Assert.Null(GateMessageParser.TryParse("m1", body));
}
