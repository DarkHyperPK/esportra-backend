using System.Buffers.Binary;
using Esportra.Core.Tournaments;
using MetadataExtractor;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.IO;
using SkiaSharp;

namespace Esportra.Api.Services;

public sealed record SponsorCreativeValidationResult(byte[] Payload, string MimeType, string Extension, int Width, int Height);

public static class SponsorCreativeImageValidator
{
    public const long MaximumBytes = 8 * 1024 * 1024;
    public const long MaximumPixels = 24_000_000;
    private static readonly IReadOnlyDictionary<SKEncodedImageFormat, (string Mime, string[] Extensions)> Formats =
        new Dictionary<SKEncodedImageFormat, (string, string[])>
        {
            [SKEncodedImageFormat.Jpeg] = ("image/jpeg", [".jpg", ".jpeg"]),
            [SKEncodedImageFormat.Png] = ("image/png", [".png"]),
            [SKEncodedImageFormat.Webp] = ("image/webp", [".webp"]),
        };

    public static async Task<SponsorCreativeValidationResult> ValidateAsync(IFormFile file, PlacementZonePolicy policy, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException($"Creative must be at most {MaximumBytes / 1024 / 1024} MB.");
        var payload = await ReadPayloadAsync(file, ct);
        var admittedFormat = IdentifyAllowedSignature(payload);
        if (admittedFormat == SKEncodedImageFormat.Png) ValidateStaticPng(payload);
        using var data = SKData.CreateCopy(payload);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Image could not be identified.");
        if (codec.EncodedFormat != admittedFormat) throw new InvalidDataException("Image format could not be identified.");
        var expected = ValidateFileMetadata(file, admittedFormat);
        var info = codec.Info;
        ValidateDimensions(info, policy);
        if (codec.FrameCount > 1) throw new InvalidDataException("Animated creatives are not supported.");
        ct.ThrowIfCancellationRequested();
        using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        // Convenience decode APIs accept incomplete input; uploads require a complete pixel decode.
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("Creative must be a valid JPEG, PNG, or WebP raster image.");
        ct.ThrowIfCancellationRequested();
        return new(payload, expected.Mime, expected.Extensions[0], info.Width, info.Height);
    }

    private static (string Mime, string[] Extensions) ValidateFileMetadata(IFormFile file, SKEncodedImageFormat format)
    {
        var expected = Formats[format];
        if (!expected.Mime.Equals(file.ContentType, StringComparison.OrdinalIgnoreCase)
            || !expected.Extensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Creative extension, MIME type, and decoded format must match.");
        return expected;
    }

    private static void ValidateDimensions(SKImageInfo info, PlacementZonePolicy policy)
    {
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaximumPixels)
            throw new InvalidDataException("Creative dimensions are too large.");
        if (!SponsorPlacementPolicy.IsAspectRatioValid(policy, info.Width, info.Height))
            throw new InvalidDataException($"Creative must be at least {policy.MinimumWidth}×{policy.MinimumHeight} and match the {policy.Zone} aspect ratio.");
    }

    private static void ValidateStaticPng(byte[] payload)
    {
        IEnumerable<PngChunk> animationChunks;
        try
        {
            // Skia's packaged PNG decoder ignores APNG chunks. Inspect only acTL using the PNG-only reader.
            animationChunks = new PngChunkReader().Extract(new BoundedPngReader(payload), [new PngChunkType("acTL")]);
        }
        catch (Exception exception) when (exception is ImageProcessingException or IOException or ArgumentException)
        {
            throw new InvalidDataException("Creative must be a valid JPEG, PNG, or WebP raster image.", exception);
        }
        if (animationChunks.Any()) throw new InvalidDataException("Animated creatives are not supported.");
    }

    private sealed class BoundedPngReader(byte[] payload) : SequentialByteArrayReader(payload)
    {
        public override byte[] GetBytes(int count)
        {
            // Bound allocations before the library's int position+length arithmetic can overflow.
            if (count < 0 || count > Available()) throw new InvalidDataException("PNG chunk exceeds upload length.");
            return base.GetBytes(count);
        }
    }

    private static async Task<byte[]> ReadPayloadAsync(IFormFile file, CancellationToken ct)
    {
        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream((int)file.Length);
        var chunk = new byte[16 * 1024];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + bytesRead > MaximumBytes)
                throw new InvalidDataException($"Creative must be at most {MaximumBytes / 1024 / 1024} MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), ct);
        }
        if (buffer.Length != file.Length) throw new InvalidDataException("Creative upload length is invalid.");
        return buffer.ToArray();
    }

    private static SKEncodedImageFormat IdentifyAllowedSignature(ReadOnlySpan<byte> payload)
    {
        // Admit fixed signatures before creating any native codec; unsupported containers never reach a decoder.
        if (payload.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return SKEncodedImageFormat.Jpeg;
        if (payload.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return SKEncodedImageFormat.Png;
        if (payload.Length >= 12 && payload[..4].SequenceEqual("RIFF"u8) && payload.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            // Native WebP decoding can succeed with a missing pad byte; enforce the container's declared length.
            if ((long)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)) + 8 != payload.Length)
                throw new InvalidDataException("WebP container length is invalid.");
            return SKEncodedImageFormat.Webp;
        }
        throw new InvalidDataException("Only JPEG, PNG, and WebP are supported.");
    }
}
