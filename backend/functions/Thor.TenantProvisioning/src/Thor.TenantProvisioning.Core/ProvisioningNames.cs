using System.Text.RegularExpressions;

namespace Thor.TenantProvisioning.Core;

/// <summary>
/// Deterministic names for the AWS resources a tenant gets. Determinism is what makes
/// the Cognito steps idempotent — a re-run resolves the existing pool/group by the same
/// name instead of creating a duplicate.
/// </summary>
public static partial class ProvisioningNames
{
    public const string AdminGroup = "admins";

    /// <summary>
    /// The SPA route Cognito redirects back to after hosted sign-in (e.g. via a federated IdP).
    /// Must match the frontend's OAuth callback route.
    /// </summary>
    public const string SignInCallbackPath = "/auth/callback";

    public static string UserPool(string subdomain) => $"thor-{subdomain}";

    public static string AppClient(string subdomain) => $"thor-{subdomain}-app";

    /// <summary>
    /// The pool's Cognito prefix domain, <c>{envPrefix}-{subdomain}</c>. Prefix domains are unique
    /// per region across every AWS account, so the environment prefix keeps dev/qa/prod tenants
    /// with the same subdomain apart. Throws if Cognito would reject the prefix, so the step fails
    /// with a clear reason rather than an opaque SDK error.
    /// </summary>
    public static string CognitoDomainPrefix(string envPrefix, string subdomain)
    {
        var prefix = $"{envPrefix}-{subdomain}".ToLowerInvariant();

        if (!DomainPrefixPattern().IsMatch(prefix))
        {
            throw new InvalidOperationException(
                $"Cognito domain prefix '{prefix}' is invalid: it must be 1-63 lowercase letters, digits or hyphens, starting and ending with a letter or digit.");
        }

        foreach (var reserved in ReservedDomainWords)
        {
            if (prefix.Contains(reserved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cognito domain prefix '{prefix}' contains the reserved word '{reserved}'.");
            }
        }

        return prefix;
    }

    /// <summary>The host of a Cognito prefix domain, e.g. <c>thor-dev-acme.auth.us-east-1.amazoncognito.com</c>.</summary>
    public static string CognitoDomainHost(string domainPrefix, string region) =>
        $"{domainPrefix}.auth.{region}.amazoncognito.com";

    // Cognito rejects prefix domains containing these words.
    private static readonly string[] ReservedDomainWords = ["aws", "amazon", "cognito"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainPrefixPattern();
}
