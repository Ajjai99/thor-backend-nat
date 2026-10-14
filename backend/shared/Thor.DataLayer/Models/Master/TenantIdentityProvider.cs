using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models;

/// <summary>
/// Thor's record of one SAML/OIDC identity provider on a tenant's Cognito user pool, keyed by
/// (<see cref="TenantId"/>, <see cref="ProviderName"/>). Cognito holds the IdP configuration and
/// every secret; this row holds only what Cognito can't: whether the IdP is enabled, the admin's
/// vendor label, and which SAML source the settings came from.
/// <para>
/// No row means disabled: an IdP created before this table existed is treated as disabled until
/// an admin enables it, and sign-in through an IdP without a row is refused (fail closed).
/// </para>
/// </summary>
[Table("tenant_identity_provider", Schema = "auth")]
public sealed class TenantIdentityProvider
{
    [Column("tenant_id")]
    [ForeignKey(nameof(Tenant))]
    public Guid TenantId { get; set; }

    [Column("provider_name")]
    public string ProviderName { get; set; } = null!;

    // Display label chosen by the admin (e.g. "entra"); one of IdentityProviderVendors in Thor.Api.
    [Column("vendor")]
    public string? Vendor { get; set; }

    // "metadataUrl", "metadataFile" or "manual" for SAML (SamlMetadataSources in Thor.Api); null for OIDC.
    [Column("saml_metadata_source")]
    public string? SamlMetadataSource { get; set; }

    [Column("enabled")]
    public bool Enabled { get; set; }

    // Cognito sub of the admin who created / last changed the IdP.
    [Column("created_by_sub")]
    public string CreatedBySub { get; set; } = null!;

    [Column("updated_by_sub")]
    public string UpdatedBySub { get; set; } = null!;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
