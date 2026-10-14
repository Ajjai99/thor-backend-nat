namespace Thor.Api.Exceptions;

/// <summary>A file was uploaded for an authentication field whose input type doesn't accept files.</summary>
public sealed class InvalidAuthenticationFileFieldException(Guid fieldId)
    : Exception($"Authentication field '{fieldId}' does not accept a file upload.")
{
    public Guid FieldId { get; } = fieldId;
}
