using System.Net.Http.Headers;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Proxied file uploads to Supabase Storage using service_role key (bypasses RLS).
/// Replaces direct supabase.storage.from(...).upload(...) calls from the frontend,
/// which broke after migrating auth away from Supabase GoTrue sessions.
/// </summary>
public static class StorageEndpoints
{
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
                return Results.BadRequest(new { error = "Query/form parameter 'bucket' is required." });

            var folder = form["folder"].FirstOrDefault() ?? "";

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            // Build unique filename
            var ext = Path.GetExtension(file.FileName);
            var uniqueName = $"{userCtx.UserIdGuid}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
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
                    new { error = "File upload failed.", detail = errorBody, url = uploadUrl },
                    statusCode: (int)response.StatusCode);
            }

            var publicUrl = $"{supabaseUrl}/storage/v1/object/public/{bucket}/{storagePath}";
            return Results.Ok(new { url = publicUrl, path = storagePath });

        }).RequireAuthorization("Authenticated")
          .DisableAntiforgery();
    }
}
