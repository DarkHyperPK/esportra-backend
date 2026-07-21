using Esportra.Api.Services;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class SponsorCreativeImageValidatorTests
{
    [Theory]
    [InlineData("wide_partner", 1600, 700, "creative.jpg", "image/jpeg")]
    [InlineData("sidebar_partner", 600, 1200, "creative.png", "image/png")]
    [InlineData("partner_logo", 600, 200, "creative.png", "image/png")]
    public async Task ValidateAsync_AcceptsValidZoneCreative(string zone, int width, int height, string name, string contentType)
    {
        SponsorPlacementPolicy.TryGet(zone, out var policy);
        var file = CreateImage(width, height, name, contentType);

        var result = await SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(contentType, result.MimeType);
    }

    [Fact]
    public async Task ValidateAsync_RejectsSpoofedMime()
    {
        SponsorPlacementPolicy.TryGet("wide_partner", out var policy);
        var file = CreateImage(1600, 700, "creative.jpg", "image/png");

        await Assert.ThrowsAsync<InvalidDataException>(() => SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsTruncatedImage()
    {
        SponsorPlacementPolicy.TryGet("wide_partner", out var policy);
        var valid = CreateImage(1600, 700, "creative.jpg", "image/jpeg");
        await using var source = valid.OpenReadStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        var bytes = buffer.ToArray()[..100];
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "creative.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

        await Assert.ThrowsAnyAsync<Exception>(() => SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsWrongAspectRatio()
    {
        SponsorPlacementPolicy.TryGet("sidebar_partner", out var policy);
        var file = CreateImage(1600, 700, "creative.jpg", "image/jpeg");

        await Assert.ThrowsAsync<InvalidDataException>(() => SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsOversizedFile()
    {
        SponsorPlacementPolicy.TryGet("wide_partner", out var policy);
        var bytes = new byte[9 * 1024 * 1024];
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "creative.jpg")
            { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

        await Assert.ThrowsAsync<InvalidDataException>(() => SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsWrongExtension()
    {
        SponsorPlacementPolicy.TryGet("wide_partner", out var policy);
        var file = CreateImage(1600, 700, "creative.png", "image/jpeg");

        await Assert.ThrowsAsync<InvalidDataException>(() => SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None));
    }

    [Theory]
    [InlineData("card_badge", 160, 48, "creative.png", "image/png")]
    [InlineData("homepage_ticker", 240, 80, "creative.jpg", "image/jpeg")]
    public async Task ValidateAsync_AcceptsLogoZones(string zone, int width, int height, string name, string contentType)
    {
        SponsorPlacementPolicy.TryGet(zone, out var policy);
        var file = CreateImage(width, height, name, contentType);

        var result = await SponsorCreativeImageValidator.ValidateAsync(file, policy, CancellationToken.None);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    private static FormFile CreateImage(int width, int height, string name, string contentType)
    {
        using var image = new Image<Rgba32>(width, height);
        var stream = new MemoryStream();
        if (contentType == "image/png" || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) image.Save(stream, new PngEncoder());
        else image.Save(stream, new JpegEncoder());
        var bytes = stream.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }
}
