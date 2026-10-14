namespace Thor.Api.Exceptions;

/// <summary>The logo upload request or the uploaded file was rejected.</summary>
public sealed class InvalidLogoException(string message) : Exception(message);
