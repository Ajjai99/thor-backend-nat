namespace Thor.Api.Exceptions;

/// <summary>The tenant has no logo.</summary>
public sealed class LogoNotFoundException() : Exception("No logo is set.");
