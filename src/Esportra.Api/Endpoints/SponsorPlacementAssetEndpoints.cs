using System.Net.Http.Headers;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;
using Esportra.Api.Services;

namespace Esportra.Api.Endpoints;

public static class SponsorPlacementAssetEndpoints
{
    private const string Bucket = "system.assets.partners";

    public static void MapSponsorPlacementAssetEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/placement-assets", UploadAsync)
            .RequireAuthorization(Permissions.SponsorsEdit)
            .DisableAntiforgery();
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var user = context.Items["UserContext"] as UserContext;
        if (user is null || (!user.IsSuperAdmin && !user.Permissions.Contains(Permissions.SponsorsEdit, StringComparer.OrdinalIgnoreCase)))
            return Results.Forbid();

        var form = await context.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        var zone = form["zone"].FirstOrDefault();
        var roleValue = form["assetRole"].FirstOrDefault();
        if (file is null)
            return Results.BadRequest(new { error = "Creative is required." });
        if (zone is null || !SponsorPlacementPolicy.TryGet(zone, out var policy))
            return Results.BadRequest(new { error = "Invalid placement zone." });
        if (!Enum.TryParse<SponsorCreativeRole>(roleValue, true, out var role) || role != policy.RequiredRole)
            return Results.BadRequest(new { error = $"{zone} requires a {policy.RequiredRole.ToString().ToLowerInvariant()} creative." });

        SponsorCreativeValidationResult validated;
        try
        {
            validated = await SponsorCreativeImageValidator.ValidateAsync(file, policy, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException or SixLabors.ImageSharp.ImageFormatException or NotSupportedException)
        {
            return Results.BadRequest(new { error = exception is InvalidDataException ? exception.Message : "Creative must be a valid JPEG, PNG, or WebP raster image." });
        }

        var mimeType = validated.MimeType;
        var extension = validated.Extension;
        var assetId = Guid.NewGuid();
        var objectPath = $"placements/{zone}/{role.ToString().ToLowerInvariant()}/{assetId:N}{extension}";
        var supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/') ?? throw new InvalidOperationException("Supabase:Url not configured");
        var serviceKey = configuration["Supabase:ServiceKey"] ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        client.DefaultRequestHeaders.Add("apikey", serviceKey);
        using var content = new ByteArrayContent(validated.Payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        using var response = await client.PostAsync($"{supabaseUrl}/storage/v1/object/{Bucket}/{objectPath}", content, ct);
        if (!response.IsSuccessStatusCode) return Results.Problem("Creative upload failed.", statusCode: 502);

        var publicUrl = $"{supabaseUrl}/storage/v1/object/public/{Bucket}/{objectPath}";
        using var connection = connectionFactory.CreateConnection();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sponsor_placement_assets
                    (id, bucket, object_path, public_url, placement_zone, asset_role, mime_type, width, height, byte_size, uploaded_by)
                VALUES (@assetId, @Bucket, @objectPath, @publicUrl, @zone, @assetRole, @mimeType, @width, @height, @byteSize, @uploadedBy)
                """,
                new { assetId, Bucket, objectPath, publicUrl, zone, assetRole = role.ToString().ToLowerInvariant(), mimeType, width = validated.Width, height = validated.Height, byteSize = validated.Payload.LongLength, uploadedBy = user.UserIdGuid },
                cancellationToken: ct));
        }
        catch
        {
            await client.DeleteAsync($"{supabaseUrl}/storage/v1/object/{Bucket}/{objectPath}", ct);
            throw;
        }

        return Results.Ok(new { assetId, url = publicUrl, width = validated.Width, height = validated.Height, mimeType });
    }
}
