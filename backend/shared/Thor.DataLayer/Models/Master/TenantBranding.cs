using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models;

/// <summary>
/// A tenant's login-page branding. One row per <see cref="Tenant"/> that has uploaded a logo;
/// absence of a row means no logo. <see cref="LogoObjectKey"/> is the key of the validated logo in
/// the tenant-assets bucket, which the frontend CloudFront distribution serves at <c>/{key}</c>.
/// </summary>
[Table("tenant_branding", Schema = "auth")]
public sealed class TenantBranding
{
    [Key]
    [Column("tenant_id")]
    [ForeignKey(nameof(Tenant))]
    public Guid TenantId { get; set; }

    [Column("logo_object_key")]
    public string LogoObjectKey { get; set; } = null!;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
