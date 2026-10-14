namespace Thor.Api.Models;

// FileName is set only by AuthenticationMethodsController.MergeFileValuesAsync, from an uploaded
// file part's own name; any FileName sent in the request JSON is discarded there.
public sealed record AuthenticationValueRequest(Guid FieldId, string Value, string? FileName = null);
