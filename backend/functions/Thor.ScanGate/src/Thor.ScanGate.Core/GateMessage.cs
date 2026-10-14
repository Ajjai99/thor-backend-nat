namespace Thor.ScanGate.Core;

/// <summary>
/// One trigger message, trigger-agnostic: a scan id when the sender knows it (ingestion's input,
/// Thor.TaskApi), otherwise the manifest it concerns (ATRE's and Ownership's input carry only that).
/// </summary>
public sealed record GateMessage(string MessageId, Guid TenantId, Guid? ScanId, Guid? ScanManifestId);
