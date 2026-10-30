using System.Security.Cryptography;
using System.Text;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using SkiaSharp;

namespace Esportra.Api.Endpoints;

public static class ProEventProofEndpoints
{
    public static void MapProEventProofEndpoints(this WebApplication app)
    {
        app.MapPost("/api/pro-event-requests/{id:guid}/proof", UploadAsync).AllowAnonymous()
            .WithMetadata(new RateLimitPolicyMetadata("auth"), new RequestSizeLimitAttribute(6 * 1024 * 1024));
        app.MapGet("/api/admin/pro-event-requests/{id:guid}/proof", ReadAsync).RequireAuthorization("admin");
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Pro-Request-Token"].ToString();
        if (!ValidToken(token)) return Results.NotFound();
        var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = 6 * 1024 * 1024;
        using var conn = db.CreateConnection();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var status = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            """
            SELECT CASE WHEN proof_image IS NOT NULL THEN 'proof_submitted' ELSE status END
              FROM pro_event_requests WHERE id = @id AND access_token_hash = @hash
              AND invoice_details IS NOT NULL AND invoice_expires_at > now()
            """, new { id, hash }, cancellationToken: ct));
        if (status == "proof_submitted") return Results.Ok(new { status });
        if (status is not ("pending" or "contacted")) return Results.NotFound();
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Upload a payment-proof image." });
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("proof");
        if (file is null || file.Length is <= 0 or > 5 * 1024 * 1024)
            return Results.BadRequest(new { error = "Choose a JPEG, PNG or WebP image under 5 MB." });
        await using var input = file.OpenReadStream();
        var image = SanitizeImage(input);
        if (image is null) return Results.BadRequest(new { error = "The file is not a supported, readable receipt image." });
        var count = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pro_event_requests SET proof_image = @image, status = 'proof_submitted', updated_at = now()
            WHERE id = @id AND access_token_hash = @hash AND invoice_expires_at > now()
              AND status IN ('pending','contacted') AND invoice_details IS NOT NULL AND proof_image IS NULL
            """, new { id, hash, image }, cancellationToken: ct));
        return count == 0 ? Results.Conflict(new { error = "This invoice no longer accepts a new proof." })
            : Results.Ok(new { status = "proof_submitted" });
    }

    private static async Task<IResult> ReadAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user || (!user.IsSuperAdmin
            && !user.Permissions.Contains(Permissions.LicensesView))) return Results.Forbid();
        using var conn = db.CreateConnection();
        var image = await conn.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT proof_image FROM pro_event_requests WHERE id = @id", new { id }, cancellationToken: ct));
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        return image is null ? Results.NotFound() : Results.File(image, "image/jpeg");
    }

    internal static bool ValidToken(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigit);

    internal static byte[]? SanitizeImage(Stream input)
    {
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)
            || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 12000000) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;
        var ratio = Math.Min(1d, 1600d / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * ratio)), Math.Max(1, (int)(bitmap.Height * ratio))), SKSamplingOptions.Default);
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return encoded?.ToArray();
    }
}
