namespace Thor.Api.Middleware;

/// <summary>
/// The caller as verified by <see cref="CognitoAuthMiddleware"/>, stored in
/// <c>HttpContext.Features</c> for the rest of the request. Taken from the validated token, never
/// from headers — a client can't set it. Absent on routes the middleware doesn't gate.
/// </summary>
/// <param name="TenantId">The tenant the token verified against.</param>
/// <param name="PrincipalId">Cognito <c>sub</c>, or the API key id for a Thor-issued connector token.</param>
/// <param name="Groups">The token's <c>cognito:groups</c>; always empty for connector tokens.</param>
public sealed record AuthenticatedCaller(Guid TenantId, string PrincipalId, IReadOnlyList<string> Groups);
