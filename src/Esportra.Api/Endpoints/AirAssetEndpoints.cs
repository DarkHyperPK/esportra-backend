using System.Security.Cryptography;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using SkiaSharp;

namespace Esportra.Api.Endpoints;

public static class AirAssetEndpoints
{
    public static void MapAirAssetEndpoints(this WebApplication app)
    {
        app.MapPost("/api/air/workspaces/{workspaceId:guid}/assets", Upload).RequireAuthorization("Authenticated");
        app.MapGet("/api/air/assets/{assetId:guid}", Download).RequireAuthorization("Authenticated");
    }
    private static async Task<IResult> Upload(Guid workspaceId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var connection = db.CreateConnection(); var userId = AirAccountEndpoints.User(ctx);
        if (!await accounts.OwnsWorkspaceAsync(connection, workspaceId, userId)) return Results.NotFound();
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Active Air access is required to upload a logo." }, statusCode: 403);
        if (ctx.Request.ContentType is not ("image/png" or "image/jpeg" or "image/webp") || ctx.Request.ContentLength is not (> 0 and <= 5242880)) return Results.BadRequest(new { error = "Upload PNG, JPG or WebP, up to 5 MB." });
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0) { if (memory.Length + read > 5242880) return Results.BadRequest(new { error = "Logo exceeds 5 MB." }); await memory.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted); }
        try
        {
            using var encoded = SKData.CreateCopy(memory.ToArray());
            using var codec = SKCodec.Create(encoded);
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp) || codec.FrameCount > 1 || codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 24000000) return Results.BadRequest(new { error = "Use a valid static logo under 24 megapixels." });
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null) return Results.BadRequest(new { error = "Logo could not be decoded." });
            var scale = Math.Min(1d, 1024d / Math.Max(bitmap.Width, bitmap.Height));
            using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear));
            if (resized is null) return Results.BadRequest(new { error = "Logo could not be resized." });
            using var image = SKImage.FromBitmap(resized); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var payload = png.ToArray(); if (payload.Length > 5242880) return Results.BadRequest(new { error = "Use a smaller logo." });
            var digest = Convert.ToHexString(SHA256.HashData(payload));
            var supplied = ctx.Request.Headers["X-Air-Asset-Id"].ToString();
            if (supplied.Length > 0 && (!Guid.TryParse(supplied, out var suppliedId) || suppliedId == Guid.Empty)) return Results.BadRequest(new { error = "Invalid asset identity." });
            var assetId = Guid.TryParse(supplied, out var parsed) ? parsed : Guid.NewGuid();
            var id = await connection.ExecuteScalarAsync<Guid>("INSERT INTO air_assets(id,workspace_id,digest,mime_type,payload) SELECT @assetId,@workspaceId,@digest,'image/png',@payload WHERE EXISTS(SELECT 1 FROM air_assets WHERE id=@assetId) OR (SELECT count(*) FROM air_assets WHERE workspace_id=@workspaceId)<256 ON CONFLICT(id) DO UPDATE SET digest=air_assets.digest WHERE air_assets.workspace_id=excluded.workspace_id AND air_assets.digest=excluded.digest RETURNING id", new { assetId, workspaceId, digest, payload });
            return id == Guid.Empty ? Results.Conflict(new { error = "Workspace asset limit reached." }) : Results.Ok(new { id, mimeType = "image/png", width = resized.Width, height = resized.Height });
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = "Use a valid PNG, JPG or WebP image." }); }
    }
    private static async Task<IResult> Download(Guid assetId, HttpContext ctx, IDbConnectionFactory db)
    {
        using var connection = db.CreateConnection();
        var bytes = await connection.QuerySingleOrDefaultAsync<byte[]>("SELECT a.payload FROM air_assets a JOIN air_workspaces w ON w.id=a.workspace_id WHERE a.id=@assetId AND w.owner_id=@userId", new { assetId, userId = AirAccountEndpoints.User(ctx) });
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
    }
}
