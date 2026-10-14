namespace Thor.ScanGate.Core;

/// <summary>One scan to evaluate. The tenant is a hint until the tenant store resolves it (ADR §5.4).</summary>
public sealed record ScanGateRequest(Guid TenantId, Guid ScanId);
