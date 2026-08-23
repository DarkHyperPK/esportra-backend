using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Supabase;

/// <summary>
/// Wraps the Supabase Auth Admin REST API.
/// Docs: https://supabase.com/docs/reference/api/admin-list-users
/// Config required: Supabase:Url, Supabase:ServiceKey
/// </summary>
public sealed class SupabaseAdminClient(
    HttpClient http,
    IConfiguration config,
    ILogger<SupabaseAdminClient> logger) : ISupabaseAdminClient
{
    private readonly string _baseUrl = config["Supabase:Url"]?.TrimEnd('/') ?? throw new InvalidOperationException("Supabase:Url is required");
    private readonly string _svcKey = config["Supabase:ServiceKey"] ?? throw new InvalidOperationException("Supabase:ServiceKey is required");

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}/auth/v1/admin{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _svcKey);
        req.Headers.Add("apikey", _svcKey);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return req;
    }

    public async Task DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        var req = BuildRequest(HttpMethod.Delete, $"/users/{userId}");
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeleteUser failed: {await res.Content.ReadAsStringAsync(ct)}");
        logger.LogInformation("[SupabaseAdmin] Deleted user {UserId}", userId);
    }

    public async Task UpdateUserAsync(string userId, object updates, CancellationToken ct = default)
    {
        var req = BuildRequest(HttpMethod.Put, $"/users/{userId}", updates);
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"UpdateUser failed: {await res.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<GeneratedLink> GenerateRecoveryLinkAsync(string email, CancellationToken ct = default)
    {
        return await GenerateLinkAsync("recovery", email,
            config["FrontendUrl"]?.TrimEnd('/') ?? "https://esportra.com", ct);
    }

    public async Task<SupabaseUser?> VerifyOtpAsync(string tokenHash, string type, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/auth/v1/verify");
        req.Headers.Add("apikey", _svcKey);
        req.Content = JsonContent.Create(new { token_hash = tokenHash, type });

        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var userId = root.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var id)
            ? id.GetString() : null;
        var email = root.TryGetProperty("user", out var u2) && u2.TryGetProperty("email", out var em)
            ? em.GetString() : null;

        return userId is not null ? new SupabaseUser(userId, email ?? "") : null;
    }

    public async Task<GeneratedLink> GenerateInviteLinkAsync(string email, string redirectUrl, CancellationToken ct = default)
    {
        return await GenerateLinkAsync("invite", email, redirectUrl, ct);
    }

    public async Task<GeneratedLink> GenerateMagicLinkAsync(string email, string redirectUrl, CancellationToken ct = default)
    {
        return await GenerateLinkAsync("magiclink", email, redirectUrl, ct);
    }

    private async Task<GeneratedLink> GenerateLinkAsync(
        string type,
        string email,
        string redirectUrl,
        CancellationToken ct)
    {
        var req = BuildRequest(HttpMethod.Post, "/generate_link", new
        {
            type,
            email = email,
            redirect_to = redirectUrl,
        });

        var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"GenerateLink failed with status {(int)res.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var tokenHash = root.TryGetProperty("hashed_token", out var ht) ? ht.GetString() ?? "" : "";
        var actionLink = root.TryGetProperty("action_link", out var al) ? al.GetString() ?? "" : "";
        var userId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

        return new GeneratedLink(tokenHash, actionLink, userId);
    }

    public async Task<SupabaseUser?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        // GoTrue admin /users endpoint does NOT support email query filtering.
        // We paginate and search manually, or use the per-page listing.
        // For reliability, iterate pages until we find a match or exhaust the list.
        int page = 1;
        const int perPage = 50;

        while (true)
        {
            var req = BuildRequest(HttpMethod.Get, $"/users?page={page}&per_page={perPage}");
            var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"GetUserByEmail failed with status {(int)res.StatusCode}.");

            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement? users = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("users", out var u) ? u : null;

            if (users is null || users.Value.GetArrayLength() == 0) return null;

            foreach (var user in users.Value.EnumerateArray())
            {
                var userEmail = user.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
                if (string.Equals(userEmail, email, StringComparison.OrdinalIgnoreCase))
                {
                    var id = user.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (id is null) return null;

                    return new SupabaseUser(id, userEmail ?? "");
                }
            }

            if (users.Value.GetArrayLength() < perPage) return null; // No more pages
            page++;
        }
    }

    public async Task<SupabaseUser> CreateUserAsync(string email, object? userMetadata = null, CancellationToken ct = default)
    {
        var req = BuildRequest(HttpMethod.Post, "/users", new
        {
            email = email,
            email_confirm = true,
            user_metadata = userMetadata,
        });

        var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"CreateUser failed: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var mail = root.TryGetProperty("email", out var em) ? em.GetString() ?? email : email;
        return new SupabaseUser(id, mail);
    }

    public async Task<SupabaseUserListResult> ListUsersAsync(int page = 1, int perPage = 50, CancellationToken ct = default)
    {
        var req = BuildRequest(HttpMethod.Get, $"/users?page={page}&per_page={perPage}");
        var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"ListUsers failed: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var total = root.TryGetProperty("total", out var tp) ? tp.GetInt32() : 0;
        var users = new List<SupabaseAuthUser>();

        JsonElement? usersEl = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("users", out var u) ? u : null;

        if (usersEl is not null)
        {
            foreach (var user in usersEl.Value.EnumerateArray())
            {
                var id = user.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var email = user.TryGetProperty("email", out var emEl) ? emEl.GetString() ?? "" : "";

                DateTimeOffset? lastSignIn = null;
                if (user.TryGetProperty("last_sign_in_at", out var lsi) && lsi.ValueKind != JsonValueKind.Null)
                    lastSignIn = lsi.GetDateTimeOffset();

                var createdAt = user.TryGetProperty("created_at", out var ca) && ca.ValueKind != JsonValueKind.Null
                    ? ca.GetDateTimeOffset()
                    : DateTimeOffset.MinValue;

                users.Add(new SupabaseAuthUser(id, email, lastSignIn, createdAt));
            }
        }

        return new SupabaseUserListResult(users, total);
    }

    public async Task LogoutUserAsync(string userId, CancellationToken ct = default)
    {
        // GoTrue admin API: ban the user briefly to invalidate all refresh tokens,
        // then immediately unban. This forces re-authentication on next token refresh.
        // Step 1: Ban for 1 second
        var banReq = BuildRequest(HttpMethod.Put, $"/users/{userId}", new
        {
            ban_duration = "1s"
        });
        var banRes = await http.SendAsync(banReq, ct);
        if (!banRes.IsSuccessStatusCode)
        {
            var banBody = await banRes.Content.ReadAsStringAsync(ct);
            logger.LogWarning("[SupabaseAdmin] Ban-to-logout failed for {UserId}: {Body}", userId, banBody);
            throw new InvalidOperationException($"LogoutUser ban step failed: {banBody}");
        }

        // Step 2: Immediately unban by setting ban_duration to "none" (clears banned_until)
        var unbanReq = BuildRequest(HttpMethod.Put, $"/users/{userId}", new
        {
            ban_duration = "none"
        });
        var unbanRes = await http.SendAsync(unbanReq, ct);
        if (!unbanRes.IsSuccessStatusCode)
        {
            var unbanBody = await unbanRes.Content.ReadAsStringAsync(ct);
            logger.LogWarning("[SupabaseAdmin] Unban-after-logout failed for {UserId}: {Body}", userId, unbanBody);
            // Don't throw — the ban will expire in 1s anyway
        }

        logger.LogInformation("[SupabaseAdmin] Force-logged-out user {UserId} (ban/unban cycle)", userId);
    }
}
