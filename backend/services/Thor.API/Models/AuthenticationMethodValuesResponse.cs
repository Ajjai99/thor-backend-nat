namespace Thor.Api.Models;

// Values are keyed by field name; a field with nothing stored is omitted. Password-family fields
// never carry their real value (a fixed placeholder marks that one is stored), and file-family
// fields carry the uploaded file's name, never its content. TypeId tells an edit form which
// type's fields to render.
public sealed record AuthenticationMethodValuesResponse(
    Guid Id,
    Guid TypeId,
    string Name,
    IReadOnlyDictionary<string, string> Values);
