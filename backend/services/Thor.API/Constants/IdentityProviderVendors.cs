namespace Thor.Api.Constants;

/// <summary>
/// The vendor labels an admin can give an identity provider. A display label only: nothing is
/// authorized on it, and Cognito never sees it.
/// </summary>
public static class IdentityProviderVendors
{
    public static readonly IReadOnlyList<string> All = ["entra", "okta", "ping", "google", "adfs", "other"];
}
