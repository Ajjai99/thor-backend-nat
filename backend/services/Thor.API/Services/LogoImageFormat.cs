namespace Thor.Api.Services;

/// <summary>
/// The image formats a tenant logo may be, identified by their leading bytes rather than by any
/// name or header the uploader chose. SVG is deliberately absent: it's script-capable, and logos are
/// served from the same origin as the SPA.
/// </summary>
public sealed record LogoImageFormat(string ContentType, string Extension)
{
    public static readonly LogoImageFormat Png = new("image/png", "png");
    public static readonly LogoImageFormat Jpeg = new("image/jpeg", "jpg");
    public static readonly LogoImageFormat WebP = new("image/webp", "webp");

    public static readonly IReadOnlyList<LogoImageFormat> All = [Png, Jpeg, WebP];

    /// <summary>Bytes needed from the start of a file for <see cref="Detect"/>.</summary>
    public const int SignatureLength = 12;

    public static LogoImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        // RIFF <size> WEBP
        if (header.Length >= SignatureLength && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        return null;
    }
}
