namespace Thor.ScanGate.Core;

/// <summary>
/// <paramref name="UploadGrace"/> is how long after a presigned URL expires the gate still treats
/// its upload as possibly in flight: S3 checks the signature when a PUT starts, so one started just
/// before expiry can finish after it.
/// </summary>
public sealed record ScanGateOptions(TimeSpan UploadGrace);
