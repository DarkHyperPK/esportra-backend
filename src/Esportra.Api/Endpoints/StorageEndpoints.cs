using System.Net.Http.Headers;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Proxied file uploads to Supabase Storage using service_role key (bypasses RLS).
/// Replaces direct supabase.storage.from(...).upload(...) calls from the frontend,
/// which broke after migrating auth away from Supabase GoTrue sessions.
/// </summary>
public static class StorageEndpoints
{
    // Allowed file extensions for uploads
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".svg", ".ico"
    };
    private static readonly HashSet<string> AllowedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".avi"
    };
    private static readonly HashSet<string> AllowedDocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".pptx", ".ppt", ".doc", ".docx"
    };
    private static readonly HashSet<string> AllowedExtensions =
        new(AllowedImageExtensions.Concat(AllowedVideoExtensions).Concat(AllowedDocExtensions), StringComparer.OrdinalIgnoreCase);

    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB (videos can be large)

    // Buckets that users are allowed to upload to
    private static readonly HashSet<string> AllowedUploadBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        "users.avatars", "teams.logos", "tournaments.banners", "tournaments.media",
        "tournaments.payment.receipts", "tournaments.disputes.evidence", "tournaments.results",
        "match-evidence", "organizer-banners", "organizer-media", "tournament-images",
        "system.assets.partners", "system.assets.website", "system.assets.games", "game-assets",
        "users.documents.kyc", "venue-images", "venues.images", "venues.layouts"
    };

    // Buckets that users are allowed to delete from
    private static readonly HashSet<string> AllowedDeleteBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        "users.avatars", "teams.logos", "organizer-banners", "organizer-media",
        "system.assets.partners", "system.assets.website", "venue-images", "venues.images", "venues.layouts",
        "tournaments.banners", "tournaments.media", "tournament-images"
    };

    public static void MapStorageEndpoints(this WebApplication app)
    {
        // ── POST /api/storage/upload ─────────────────────────────────────────
        app.MapPost("/api/storage/upload", async (
            HttpContext    ctx,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided." });

            var bucket = form["bucket"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(bucket))
                return Results.BadRequest(new { error = "Please specify a storage location." });

            if (!AllowedUploadBuckets.Contains(bucket))
                return Results.BadRequest(new { error = "You're not allowed to upload to this location." });

            // Validate file size
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest(new { error = $"File exceeds maximum size of {MaxFileSizeBytes / (1024 * 1024)}MB." });

            // Validate file extension
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                return Results.BadRequest(new { error = "File type not allowed. Accepted: images (jpg, png, gif, webp, svg), videos (mp4, mov, webm), and documents (pdf, pptx)." });

            var folder = form["folder"].FirstOrDefault() ?? "";

            if (!await ValidateStorageUploadAsync(userCtx, bucket, folder, ctx.RequestServices))
                return Results.Forbid();

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            // Build filename: keep original name, prefix with timestamp for uniqueness
            var sanitized = Path.GetFileNameWithoutExtension(file.FileName)
                .Replace(' ', '-').Replace('/', '-').Replace('\\', '-');
            var uniqueName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{sanitized}{ext}";
            var storagePath = string.IsNullOrWhiteSpace(folder)
                ? uniqueName
                : $"{folder.Trim('/')}/{uniqueName}";

            // Upload to Supabase Storage REST API
            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            client.DefaultRequestHeaders.Add("apikey", serviceKey);
            client.DefaultRequestHeaders.Add("x-upsert", "true");

            using var stream = file.OpenReadStream();
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

            var uploadUrl = $"{supabaseUrl}/storage/v1/object/{bucket}/{storagePath}";
            var response = await client.PostAsync(uploadUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                logger.LogError("Supabase storage upload failed: {Url} {Status} {Body}",
                    uploadUrl, response.StatusCode, errorBody);
                return Results.Json(
                    new { error = "File upload failed." },
                    statusCode: (int)response.StatusCode);
            }

            var publicUrl = $"{supabaseUrl}/storage/v1/object/public/{bucket}/{storagePath}";
            return Results.Ok(new { url = publicUrl, path = storagePath });

        }).RequireAuthorization("Authenticated")
          .DisableAntiforgery();

        // ── POST /api/storage/upload-player-card ─────────────────────────────
        // Uploads to: user.avatars / Player-cards / {teamName} / {filename}
        app.MapPost("/api/storage/upload-player-card", async (
            HttpContext         ctx,
            IHttpClientFactory  httpFactory,
            IConfiguration      config,
            IDbConnectionFactory db,
            ILogger<Program>   logger) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided." });

            var teamIdStr = form["teamId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(teamIdStr) || !Guid.TryParse(teamIdStr, out var teamId))
                return Results.BadRequest(new { error = "Form parameter 'teamId' is required." });

            using var conn = db.CreateConnection();
            var teamName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM teams WHERE id = @teamId", new { teamId });

            if (string.IsNullOrWhiteSpace(teamName))
                return Results.NotFound(new { error = "Team not found." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            var ext = Path.GetExtension(file.FileName);
            var uniqueName = $"{userCtx.UserIdGuid}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
            var storagePath = $"Player-cards/{teamName}/{uniqueName}";

            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            client.DefaultRequestHeaders.Add("apikey", serviceKey);
            client.DefaultRequestHeaders.Add("x-upsert", "true");

            using var stream = file.OpenReadStream();
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

            const string bucket = "users.avatars";
            var uploadUrl = $"{supabaseUrl}/storage/v1/object/{bucket}/{storagePath}";
            var response = await client.PostAsync(uploadUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                logger.LogError("Player card upload failed: {Url} {Status} {Body}",
                    uploadUrl, response.StatusCode, errorBody);
                return Results.Json(
                    new { error = "File upload failed." },
                    statusCode: (int)response.StatusCode);
            }

            var publicUrl = $"{supabaseUrl}/storage/v1/object/public/{bucket}/{storagePath}";

            // Update the profile card_image_url
            await conn.ExecuteAsync(
                "UPDATE profiles SET card_image_url = @url WHERE id = @id",
                new { url = publicUrl, id = userCtx.UserIdGuid });

            return Results.Ok(new { url = publicUrl, path = storagePath });

        }).RequireAuthorization("Authenticated")
          .DisableAntiforgery();

        // ── DELETE /api/storage/delete ────────────────────────────────────────
        app.MapDelete("/api/storage/delete", async (
            HttpContext         ctx,
            IHttpClientFactory  httpFactory,
            IConfiguration      config,
            ILogger<Program>   logger) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var bucket = ctx.Request.Query["bucket"].FirstOrDefault();
            var path   = ctx.Request.Query["path"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Please specify the file location." });

            // Restrict which buckets users can delete from
            if (!AllowedDeleteBuckets.Contains(bucket))
                return Results.Forbid();

            // Prevent path traversal
            if (path.Contains("..") || path.Contains('\0'))
                return Results.BadRequest(new { error = "Invalid path." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            client.DefaultRequestHeaders.Add("apikey", serviceKey);

            var deleteUrl = $"{supabaseUrl}/storage/v1/object/{bucket}/{path}";
            var response  = await client.DeleteAsync(deleteUrl);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Storage delete failed: {Url} {Status}",
                    deleteUrl, response.StatusCode);
            }

            return Results.Ok(new { deleted = true });

        }).RequireAuthorization("Authenticated");
    }

    private static async Task<bool> ValidateStorageUploadAsync(
        UserContext userCtx,
        string bucket,
        string folder,
        IServiceProvider services)
    {
        var userId = userCtx.UserId.ToString();
        var normalizedFolder = folder.Replace('\\', '/').Trim('/');

        if (bucket.Equals("tournaments.disputes.evidence", StringComparison.OrdinalIgnoreCase)
            || bucket.Equals("match-evidence", StringComparison.OrdinalIgnoreCase))
        {
            if (userCtx.IsSuperAdmin
                || userCtx.Permissions.Contains(Permissions.DisputesView, StringComparer.OrdinalIgnoreCase)
                || userCtx.Permissions.Contains(Permissions.DisputesResolve, StringComparer.OrdinalIgnoreCase))
                return true;

            if (normalizedFolder.StartsWith("temp/", StringComparison.OrdinalIgnoreCase))
                return normalizedFolder.Contains(userId, StringComparison.OrdinalIgnoreCase);

            // Match-result dispute evidence (uploaded before tournament_dispute row exists)
            if (normalizedFolder.StartsWith("matches/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && Guid.TryParse(parts[1], out var matchId))
                {
                    var db = services.GetRequiredService<IDbConnectionFactory>();
                    using var conn = db.CreateConnection();
                    return await conn.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS(
                            SELECT 1 FROM brkt_matches bm
                            JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                            WHERE bm.id = @matchId
                              AND tm.user_id = @userId
                              AND tm.is_active = true
                        )
                        OR EXISTS(
                            SELECT 1 FROM match_result_reports mrr
                            WHERE mrr.match_id = @matchId AND mrr.reported_by = @userId
                        )
                        """,
                        new { matchId, userId = userCtx.UserIdGuid });
                }
            }

            if (normalizedFolder.Contains(userId, StringComparison.OrdinalIgnoreCase))
                return true;

            var firstSegment = normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstSegment is not null && Guid.TryParse(firstSegment, out var disputeId))
            {
                var db = services.GetRequiredService<IDbConnectionFactory>();
                using var conn = db.CreateConnection();
                return await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM tournament_disputes
                        WHERE id = @disputeId AND raised_by_user_id = @userId
                    )
                    """,
                    new { disputeId, userId = userCtx.UserIdGuid });
            }

            return false;
        }

        if (bucket.Equals("users.documents.kyc", StringComparison.OrdinalIgnoreCase))
        {
            return userCtx.IsSuperAdmin
                || userCtx.Permissions.Contains(Permissions.UsersView, StringComparer.OrdinalIgnoreCase);
        }

        if (bucket.StartsWith("users.", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(normalizedFolder)
                || normalizedFolder.Contains(userId, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}
