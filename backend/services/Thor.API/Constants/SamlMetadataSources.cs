namespace Thor.Api.Constants;

/// <summary>
/// Which of <c>saml.metadataUrl</c> / <c>saml.metadataFile</c> / <c>saml.manual</c> a SAML IdP was
/// configured from, named after those request fields. Cognito keeps a file and manual entry alike
/// as a metadata document, so only Thor's record can tell them apart.
/// </summary>
public static class SamlMetadataSources
{
    public const string MetadataUrl = "metadataUrl";
    public const string MetadataFile = "metadataFile";
    public const string Manual = "manual";
}
