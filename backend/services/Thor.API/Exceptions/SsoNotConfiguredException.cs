namespace Thor.Api.Exceptions;

/// <summary>
/// The tenant's user pool has no Cognito domain yet (it was provisioned before domains existed),
/// so federated sign-in has no redirect endpoints. Re-running tenant provisioning adds one.
/// </summary>
public sealed class SsoNotConfiguredException()
    : Exception("Single sign-on isn't set up for this tenant yet.");
