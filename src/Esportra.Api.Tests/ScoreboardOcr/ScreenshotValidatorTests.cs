using Esportra.Api.ScoreboardOcr;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

public sealed class ScreenshotValidatorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    [Theory]
    [InlineData("shot.png", "image/png", ".png")]
    [InlineData("shot.JPEG", "image/jpeg", ".jpg")]
    [InlineData("shot.webp", "image/webp", ".webp")]
    public void Validate_AcceptsSupportedImages_WhenSignatureMatches(string name, string contentType, string ext)
    {
        var data = contentType switch { "image/png" => Png, "image/jpeg" => Jpeg, _ => Webp };

        var result = ScreenshotValidator.Validate(name, data);

        result.Ok.Should().BeTrue();
        result.ContentType.Should().Be(contentType);
        result.Extension.Should().Be(ext);
    }

    [Fact]
    public void Validate_Rejects_WhenExtensionLiesAboutContent()
    {
        ScreenshotValidator.Validate("shot.png", Jpeg).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("shot.gif")]
    [InlineData("shot.svg")]
    [InlineData("shot")]
    public void Validate_Rejects_UnsupportedExtensions(string name)
    {
        ScreenshotValidator.Validate(name, Png).Ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_Rejects_EmptyAndOversizedFiles()
    {
        ScreenshotValidator.Validate("shot.png", []).Ok.Should().BeFalse();
        var big = new byte[ScreenshotValidator.MaxBytes + 1];
        Png.CopyTo(big, 0);
        ScreenshotValidator.Validate("shot.png", big).Ok.Should().BeFalse();
    }
}
