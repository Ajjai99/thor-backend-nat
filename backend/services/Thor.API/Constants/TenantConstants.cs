namespace Thor.Api.Constants;

/// <summary>Trusted headers set by the Lambda authorizer for tenant-scoped endpoints.</summary>
public static class TenantConstants
{
    public const string TenantHeaderName = "X-THOR-TENANT-ID";

    public const string ActorHeaderName = "X-THOR-ACTOR-ID";

    /// <summary>
    /// Tenant admin group (<see cref="Models.TenantRole.Administrator"/>). Must match
    /// ProvisioningNames.AdminGroup in Thor.TenantProvisioning, which creates it and adds the
    /// tenant's first admin to it.
    /// </summary>
    public const string AdminGroup = "admins";

    /// <summary>Group for <see cref="Models.TenantRole.Analyst"/>. Not yet created by provisioning.</summary>
    public const string AnalystGroup = "analysts";

    /// <summary>Group for <see cref="Models.TenantRole.User"/>. Not yet created by provisioning.</summary>
    public const string UserGroup = "users";
}
