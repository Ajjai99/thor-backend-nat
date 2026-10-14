using System.Text;
using FluentAssertions;
using Thor.Api.Services;

namespace Thor.Api.Test.Services;

public class LogoImageFormatTests
{
    public static TheoryData<byte[], string> SupportedHeaders() => new()
    {
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D], "image/png" },
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1], "image/jpeg" },
        { [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBP"u8], "image/webp" },
    };

    [Theory]
    [MemberData(nameof(SupportedHeaders))]
    public void Detect_RecognisesSupportedImages(byte[] header, string contentType) =>
        LogoImageFormat.Detect(header)!.ContentType.Should().Be(contentType);

    [Theory]
    [InlineData("<svg xmlns=\"")]  // script-capable, so never accepted
    [InlineData("<html><scrip")]
    [InlineData("GIF89a......")]
    [InlineData("RIFF....WAVE")]
    [InlineData("")]
    public void Detect_RejectsAnythingElse(string header) =>
        LogoImageFormat.Detect(Encoding.ASCII.GetBytes(header)).Should().BeNull();

    [Fact]
    public void Detect_RejectsATruncatedSignature() =>
        LogoImageFormat.Detect([0x89, 0x50, 0x4E]).Should().BeNull();
}
