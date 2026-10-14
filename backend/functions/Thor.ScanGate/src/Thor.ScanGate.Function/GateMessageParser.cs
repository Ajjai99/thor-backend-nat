using System.Text.Json;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>
/// Reads a queue message body in either shape the gate is sent:
/// <list type="bullet">
/// <item>an EventBridge "Step Functions Execution Status Change" event, whose <c>detail.input</c>
/// is the finished execution's own input as a JSON string — ingestion's carries <c>TenantId</c>,
/// <c>ScanId</c> and <c>ScanManifestId</c>; the graph load's <c>TenantId</c> and <c>ScanId</c>;
/// ATRE's and Ownership's <c>TenantId</c> and <c>ScanManifestId</c>;</item>
/// <item>a direct <c>{ "TenantId": …, "ScanId": … }</c>, as Thor.TaskApi sends on task completion.</item>
/// </list>
/// </summary>
public static class GateMessageParser
{
    public static GateMessage? TryParse(string messageId, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("detail", out var detail))
            {
                return FromPayload(messageId, root);
            }

            if (detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            using var inputDocument = JsonDocument.Parse(input.GetString()!);
            return FromPayload(messageId, inputDocument.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GateMessage? FromPayload(string messageId, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || ReadGuid(payload, "TenantId") is not { } tenantId)
        {
            return null;
        }

        var scanId = ReadGuid(payload, "ScanId");
        var scanManifestId = ReadGuid(payload, "ScanManifestId");
        return scanId is null && scanManifestId is null
            ? null
            : new GateMessage(messageId, tenantId, scanId, scanManifestId);
    }

    private static Guid? ReadGuid(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var guid)
            ? guid
            : null;
}
