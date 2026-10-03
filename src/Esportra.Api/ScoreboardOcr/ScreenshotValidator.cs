namespace Esportra.Api.ScoreboardOcr;

/// <summary>Validates a scoreboard screenshot by size, extension and file signature (magic bytes).</summary>
public static class ScreenshotValidator
{
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    public sealed record Result(bool Ok, string? Error, string Extension, string ContentType);

    public static Result Validate(string fileName, ReadOnlySpan<byte> data)
    {
        var ext = Path.GetExtension(fileName);
        if (data.Length == 0)
            return Fail("No screenshot provided.");
        if (data.Length > MaxBytes)
            return Fail("Screenshot must be 10 MB or smaller.");
        if (string.IsNullOrEmpty(ext) || !ContentTypes.TryGetValue(ext, out var contentType))
            return Fail("Upload a PNG, JPG or WebP screenshot.");
        if (!SignatureMatches(contentType, data))
            return Fail("The file content does not match its image type.");
        return new Result(true, null, ext.ToLowerInvariant() == ".jpeg" ? ".jpg" : ext.ToLowerInvariant(), contentType);
    }

    private static Result Fail(string error) => new(false, error, string.Empty, string.Empty);

    private static bool SignatureMatches(string contentType, ReadOnlySpan<byte> data) => contentType switch
    {
        "image/png" => data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/webp" => data.Length >= 12
            && data[..4].SequenceEqual("RIFF"u8)
            && data.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}
