using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// One scan whose change events a PAI run read. Kept after the run: a scan is covered — its changes
/// are reflected in <c>pai_result</c> — once a run that read it has completed, and each run reads
/// only scans nothing has covered yet.
/// </summary>
[Table("pai_run_scan")]
[PrimaryKey(nameof(RunId), nameof(ScanId))]
[Index(nameof(ScanId))]
public sealed class PaiRunScan
{
    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("scan_id")]
    public Guid ScanId { get; set; }
}
