namespace Thor.ScanGate.Core;

/// <summary>Asks the uploads bucket whether an object exists.</summary>
public interface IUploadObjectProbe
{
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
