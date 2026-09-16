using System.Data;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.Helpers;
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
    private static readonly HashSet<string> AllowedPlacementImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".avif"
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
        "system.assets.website", "system.assets.games", "game-assets",
        "users.documents.kyc", "venue-images", "venues.images", "venues.layouts",
        "system.assets.partners"
    };

    // Buckets that users are allowed to delete from
    private static readonly HashSet<string> AllowedDeleteBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        "users.avatars", "teams.logos", "organizer-banners", "organizer-media",
        "system.assets.website", "venue-images", "venues.images", "venues.layouts",
        "tournaments.banners", "tournaments.media", "tournament-images"
    };

    public static void MapStorageEndpoints(this WebApplication app)
    {
        // ── POST /api/storage/upload ─────────────────────────────────────────
        app.MapPost("/api/storage/upload", async (
            HttpContext ctx,
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

            if (folder.Contains('%'))
                return Results.BadRequest(new { error = "Invalid storage folder." });

            if (IsPlacementAsset(bucket, folder)
                && (!userCtx.Permissions.Contains(Permissions.SponsorsEdit, StringComparer.OrdinalIgnoreCase)
                    || !AllowedPlacementImageExtensions.Contains(ext)
                    || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                return Results.Forbid();

            var uploadAllowed = await ValidateStorageUploadAsync(userCtx, bucket, folder, ctx.RequestServices);
            if (!uploadAllowed)
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
            HttpContext ctx,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            IDbConnectionFactory db,
            ILogger<Program> logger) =>
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

            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest(new { error = $"File exceeds maximum size of {MaxFileSizeBytes / (1024 * 1024)}MB." });

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext) || !AllowedImageExtensions.Contains(ext))
                return Results.BadRequest(new { error = "File type not allowed. Accepted: images (jpg, png, gif, webp, svg)." });

            using var conn = db.CreateConnection();

            var isActiveMember = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM team_members
                    WHERE team_id = @teamId
                      AND user_id = @userId
                      AND is_active = true
                )
                """,
                new { teamId, userId = userCtx.UserIdGuid });

            if (!isActiveMember)
                return Results.Forbid();

            var teamName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM teams WHERE id = @teamId", new { teamId });

            if (string.IsNullOrWhiteSpace(teamName))
                return Results.NotFound(new { error = "Team not found." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            var teamSlug = SanitizeTeamSlug(teamName);
            var uniqueName = $"{userCtx.UserIdGuid}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
            var storagePath = $"Player-cards/{teamSlug}/{uniqueName}";

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
            HttpContext ctx,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var bucket = ctx.Request.Query["bucket"].FirstOrDefault();
            var path = ctx.Request.Query["path"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Please specify the file location." });

            // Restrict which buckets users can delete from
            if (!AllowedDeleteBuckets.Contains(bucket))
                return Results.Forbid();

            if (IsPlacementAsset(bucket, path))
                return Results.Forbid();

            // Prevent path traversal
            if (path.Contains("..") || path.Contains('\0'))
                return Results.BadRequest(new { error = "Invalid path." });

            if (path.Contains('%'))
                return Results.BadRequest(new { error = "Invalid path." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            client.DefaultRequestHeaders.Add("apikey", serviceKey);

            var deleteUrl = $"{supabaseUrl}/storage/v1/object/{bucket}/{path}";
            var response = await client.DeleteAsync(deleteUrl);

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
        var normalizedFolder = folder.Replace('\\', '/').Trim('/');

        if (IsDisputeEvidenceBucket(bucket))
        {
            var db = services.GetRequiredService<IDbConnectionFactory>();
            using var conn = db.CreateConnection();
            return await ValidateDisputeEvidenceUploadAsync(conn, userCtx, normalizedFolder);
        }

        if (bucket.Equals("users.documents.kyc", StringComparison.OrdinalIgnoreCase))
            return userCtx.IsSuperAdmin
                || userCtx.Permissions.Contains(Permissions.UsersView, StringComparer.OrdinalIgnoreCase);

        if (IsPlacementAsset(bucket, normalizedFolder))
            return userCtx.Permissions.Contains(Permissions.SponsorsEdit, StringComparer.OrdinalIgnoreCase);

        if (bucket.StartsWith("users.", StringComparison.OrdinalIgnoreCase))
            return await ValidateUsersBucketUploadAsync(userCtx, bucket, normalizedFolder, services);

        return true;
    }

    private static bool IsDisputeEvidenceBucket(string bucket) =>
        bucket.Equals("tournaments.disputes.evidence", StringComparison.OrdinalIgnoreCase)
        || bucket.Equals("match-evidence", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> ValidateDisputeEvidenceUploadAsync(
        IDbConnection conn, UserContext userCtx, string normalizedFolder)
    {
        if (HasDisputeStaffAccess(userCtx)) return true;

        var userId = userCtx.UserId.ToString();

        if (normalizedFolder.StartsWith("temp/", StringComparison.OrdinalIgnoreCase))
            return normalizedFolder.Contains(userId, StringComparison.OrdinalIgnoreCase);

        // Match-result dispute evidence (uploaded before tournament_dispute row exists)
        if (normalizedFolder.StartsWith("matches/", StringComparison.OrdinalIgnoreCase))
            return await ValidateMatchFolderEvidenceAsync(conn, userCtx.UserIdGuid, normalizedFolder);

        if (normalizedFolder.Contains(userId, StringComparison.OrdinalIgnoreCase))
            return true;

        var firstSegment = normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstSegment is not null && Guid.TryParse(firstSegment, out var disputeId))
            return await IsDisputeParticipantAsync(conn, disputeId, userCtx.UserIdGuid);

        return false;
    }

    private static bool HasDisputeStaffAccess(UserContext userCtx) =>
        userCtx.IsSuperAdmin
        || userCtx.Permissions.Contains(Permissions.DisputesView, StringComparer.OrdinalIgnoreCase)
        || userCtx.Permissions.Contains(Permissions.DisputesResolve, StringComparer.OrdinalIgnoreCase)
        || userCtx.Permissions.Contains(StaffAuthHelper.PermDisputesAssist, StringComparer.OrdinalIgnoreCase);

    private static async Task<bool> ValidateMatchFolderEvidenceAsync(
        IDbConnection conn, Guid userId, string normalizedFolder)
    {
        var parts = normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Guid.TryParse(parts[1], out var matchId))
            return false;

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
            new { matchId, userId });
    }

    private static async Task<bool> IsDisputeParticipantAsync(
        IDbConnection conn, Guid disputeId, Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM tournament_disputes
                WHERE id = @disputeId AND raised_by_user_id = @userId
            )
            OR EXISTS(
                SELECT 1 FROM tournament_disputes td
                JOIN tournaments t ON t.id = td.tournament_id
                WHERE td.id = @disputeId AND t.organizer_id = @userId
            )
            OR EXISTS(
                SELECT 1 FROM tournament_disputes td
                JOIN brkt_matches bm ON bm.id = td.match_id
                JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                WHERE td.id = @disputeId
                  AND tm.user_id = @userId
                  AND tm.is_active = true
            )
            """,
            new { disputeId, userId });
    }

    private static async Task<bool> ValidateUsersBucketUploadAsync(
        UserContext userCtx, string bucket, string normalizedFolder, IServiceProvider services)
    {
        var userId = userCtx.UserId.ToString();

        if (normalizedFolder.StartsWith("Player-cards/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = normalizedFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return false;

            return await IsActiveMemberOfTeamWithSlugAsync(userCtx.UserIdGuid, segments[1], services);
        }

        if (string.IsNullOrWhiteSpace(normalizedFolder)
            || normalizedFolder.Contains(userId, StringComparison.OrdinalIgnoreCase))
            return true;

        // Allow known avatar folders (files are namespaced by userId in filename)
        return IsKnownAvatarFolder(bucket, normalizedFolder);
    }

    private static bool IsKnownAvatarFolder(string bucket, string normalizedFolder) =>
        bucket.Equals("users.avatars", StringComparison.OrdinalIgnoreCase)
        && (normalizedFolder.Equals("profile-pictures", StringComparison.OrdinalIgnoreCase)
            || normalizedFolder.Equals("avatars", StringComparison.OrdinalIgnoreCase));

    private static bool IsPlacementAsset(string bucket, string folder) =>
        bucket.Equals("system.assets.partners", StringComparison.OrdinalIgnoreCase)
        && folder.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?.Equals("placements", StringComparison.OrdinalIgnoreCase) == true;

    private static string SanitizeTeamSlug(string teamName) =>
        Regex.Replace(teamName, @"[^a-z0-9]", "_", RegexOptions.IgnoreCase).ToLowerInvariant();

    private static async Task<bool> IsActiveMemberOfTeamWithSlugAsync(
        Guid userId,
        string teamSlug,
        IServiceProvider services)
    {
        var db = services.GetRequiredService<IDbConnectionFactory>();
        using var conn = db.CreateConnection();

        var teams = await conn.QueryAsync<string>(
            """
            SELECT t.name
            FROM teams t
            JOIN team_members tm ON tm.team_id = t.id
            WHERE tm.user_id = @userId
              AND tm.is_active = true
            """,
            new { userId });

        return teams.Any(name => SanitizeTeamSlug(name) == teamSlug);
    }
}
