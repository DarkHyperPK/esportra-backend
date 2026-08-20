using Esportra.Core.Tournaments;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Esportra.Api.Services;

public sealed record SponsorCreativeValidationResult(byte[] Payload, string MimeType, string Extension, int Width, int Height);

public static class SponsorCreativeImageValidator
{
    public const long MaximumBytes = 8 * 1024 * 1024;
    public const long MaximumPixels = 24_000_000;
    private static readonly IReadOnlyDictionary<string, (string Mime, string[] Extensions)> Formats =
        new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["JPEG"] = ("image/jpeg", [".jpg", ".jpeg"]),
            ["PNG"] = ("image/png", [".png"]),
            ["WEBP"] = ("image/webp", [".webp"]),
        };

    public static async Task<SponsorCreativeValidationResult> ValidateAsync(IFormFile file, PlacementZonePolicy policy, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException($"Creative must be at most {MaximumBytes / 1024 / 1024} MB.");
        await using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        var payload = buffer.ToArray();
        var options = new DecoderOptions();
        await using var identifyStream = new MemoryStream(payload, false);
        var info = await Image.IdentifyAsync(options, identifyStream, ct) ?? throw new InvalidDataException("Image could not be identified.");
        var format = info.Metadata.DecodedImageFormat ?? throw new InvalidDataException("Image format could not be identified.");
        if (!Formats.TryGetValue(format.Name, out var expected)) throw new InvalidDataException("Only JPEG, PNG, and WebP are supported.");
        if (!expected.Mime.Equals(file.ContentType, StringComparison.OrdinalIgnoreCase)
            || !expected.Extensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Creative extension, MIME type, and decoded format must match.");
        if ((long)info.Width * info.Height > MaximumPixels) throw new InvalidDataException("Creative dimensions are too large.");
        if (!SponsorPlacementPolicy.IsAspectRatioValid(policy, info.Width, info.Height))
            throw new InvalidDataException($"Creative must be at least {policy.MinimumWidth}×{policy.MinimumHeight} and match the {policy.Zone} aspect ratio.");

        await using var decodeStream = new MemoryStream(payload, false);
        using var decoded = await Image.LoadAsync(options, decodeStream, ct);
        if (decoded.Frames.Count != 1) throw new InvalidDataException("Animated creatives are not supported.");
        return new(payload, expected.Mime, expected.Extensions[0], info.Width, info.Height);
    }
}
