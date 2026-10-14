namespace Thor.Api.Models;

/// <summary>
/// SAML 2.0 IdP settings. Exactly one of <see cref="MetadataUrl"/> / <see cref="MetadataFile"/> /
/// <see cref="Manual"/> is required — <see cref="Manual"/> is for IdPs whose admin console gives an
/// entity ID, SSO URL and signing certificate rather than a metadata document.
/// <para>
/// <see cref="EncryptedResponses"/> makes Cognito require assertions encrypted with its per-IdP
/// encryption certificate; <see cref="SignRequests"/> makes Cognito sign its SAML requests. Both
/// default to on for a new IdP; on update, leaving one out keeps the current setting.
/// </para>
/// </summary>
public sealed record SamlProviderSettings(
    string? MetadataUrl,
    string? MetadataFile,
    SamlManualSettings? Manual,
    bool? IdpSignout,
    bool? EncryptedResponses = null,
    bool? SignRequests = null);
