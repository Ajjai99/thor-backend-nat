using Amazon.CognitoIdentityProvider.Model;

namespace Thor.Cognito;

/// <summary>
/// Builds an <see cref="UpdateUserPoolClientRequest"/> that copies every updatable field from the
/// client's current settings. Cognito's UpdateUserPoolClient resets every omitted setting to its
/// default (token validity, auth flows, OAuth settings, enabled IdPs, ...), so callers start from this
/// copy and override only the fields they mean to change.
/// UserPoolClientUpdateFactoryTests fails if an SDK upgrade adds a field this doesn't copy.
/// </summary>
public static class UserPoolClientUpdateFactory
{
    public static UpdateUserPoolClientRequest CopyOf(UserPoolClientType client) => new()
    {
        UserPoolId = client.UserPoolId,
        ClientId = client.ClientId,

        AccessTokenValidity = client.AccessTokenValidity,
        AllowedOAuthFlows = client.AllowedOAuthFlows,
        AllowedOAuthFlowsUserPoolClient = client.AllowedOAuthFlowsUserPoolClient,
        AllowedOAuthScopes = client.AllowedOAuthScopes,
        AnalyticsConfiguration = client.AnalyticsConfiguration,
        AuthSessionValidity = client.AuthSessionValidity,
        CallbackURLs = client.CallbackURLs,
        ClientName = client.ClientName,
        DefaultRedirectURI = client.DefaultRedirectURI,
        EnablePropagateAdditionalUserContextData = client.EnablePropagateAdditionalUserContextData,
        EnableTokenRevocation = client.EnableTokenRevocation,
        ExplicitAuthFlows = client.ExplicitAuthFlows,
        IdTokenValidity = client.IdTokenValidity,
        LogoutURLs = client.LogoutURLs,
        PreventUserExistenceErrors = client.PreventUserExistenceErrors,
        ReadAttributes = client.ReadAttributes,
        RefreshTokenRotation = client.RefreshTokenRotation,
        RefreshTokenValidity = client.RefreshTokenValidity,
        SupportedIdentityProviders = client.SupportedIdentityProviders,
        TokenValidityUnits = client.TokenValidityUnits,
        WriteAttributes = client.WriteAttributes,
    };
}
