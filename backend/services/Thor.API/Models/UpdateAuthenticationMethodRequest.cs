namespace Thor.Api.Models;

// TypeId may differ from the method's current type: an update can re-type a method in place
// (see AuthenticationMethodService.UpdateAsync).
public sealed record UpdateAuthenticationMethodRequest(
    Guid TypeId,
    string Name,
    string? Description,
    IReadOnlyList<AuthenticationValueRequest> Values);
