using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Steam account linking via Steam OpenID 2.0.
/// Provides endpoints to link/unlink Steam accounts and retrieve
/// linked Steam IDs for team rosters (MatchZy config generation).
/// </summary>
public static class SteamAccountEndpoints
{
    private const string SteamOpenIdEndpoint = "https://steamcommunity.com/openid/login";
    private const string SteamPlayerSummaryUrl = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";
    private const string SteamClaimedIdPrefix = "https://steamcommunity.com/openid/id/";

    private static string ResolveFrontendUrl(IConfiguration config) =>
        config["App:FrontendUrl"]
        ?? config["FrontendUrl"]
        ?? config["Frontend:BaseUrl"]
        ?? "https://staging.esportra.com";

    private static string ResolveBackendUrl(IConfiguration config) =>
        config["App:BaseUrl"]
        ?? config["BackendUrl"]
        ?? config["Backend:BaseUrl"]
        ?? "https://api-staging.esportra.com";

    public static void MapSteamAccountEndpoints(this WebApplication app)
    {
        // =====================================================================
        //  GET /api/accounts/steam — Get linked Steam account
        // =====================================================================
        app.MapGet("/api/accounts/steam", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var account = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT steam64_id, steam_name, avatar_url, profile_url,
                       linked_at, verified
                FROM public.player_steam_accounts
                WHERE user_id = @userId
                """, new { userId = userCtx.UserIdGuid });

            if (account is null) return Results.NotFound(new { error = "No Steam account linked." });

            return Results.Ok(new
            {
                steam64Id  = (string?)account.steam64_id,
                steamName  = (string?)account.steam_name,
                avatarUrl  = (string?)account.avatar_url,
                profileUrl = (string?)account.profile_url,
                linkedAt   = (DateTime?)account.linked_at,
                verified   = (bool?)account.verified
            });
        }).RequireAuthorization("Authenticated");

        // =====================================================================
        //  GET /api/accounts/steam/auth — Initiate Steam OpenID login
        // =====================================================================
        app.MapGet("/api/accounts/steam/auth", (
            HttpContext    ctx,
            IConfiguration config) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var backendUrl = ResolveBackendUrl(config);
            var returnTo   = $"{backendUrl}/api/accounts/steam/callback?userId={userCtx.UserId}";
            var realm      = backendUrl.EndsWith('/') ? backendUrl : $"{backendUrl}/";

            var queryParams = new Dictionary<string, string>
            {
                ["openid.ns"]         = "http://specs.openid.net/auth/2.0",
                ["openid.mode"]       = "checkid_setup",
                ["openid.return_to"]  = returnTo,
                ["openid.realm"]      = realm,
                ["openid.identity"]   = "http://specs.openid.net/auth/2.0/identifier_select",
                ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select"
            };

            var redirectUrl = SteamOpenIdEndpoint + "?" +
                string.Join("&", queryParams.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            return Results.Redirect(redirectUrl);
        }).RequireAuthorization("Authenticated");

        // =====================================================================
        //  GET /api/accounts/steam/callback — Steam OpenID callback
        // =====================================================================
        //  Public — Steam redirects here with no JWT.
        app.MapGet("/api/accounts/steam/callback", async (
            HttpContext          ctx,
            IConfiguration       config,
            IDbConnectionFactory db,
            HttpClient           http,
            ILogger<Program>     logger,
            CancellationToken    ct) =>
        {
            var frontendUrl = ResolveFrontendUrl(config);

            // ── Extract userId from query string ──────────────────────────────
            var userIdStr = ctx.Request.Query["userId"].FirstOrDefault();
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            {
                logger.LogWarning("Steam callback: missing or invalid userId parameter");
                return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=invalid_user");
            }

            // ── Extract and validate openid.claimed_id ────────────────────────
            var claimedId = ctx.Request.Query["openid.claimed_id"].FirstOrDefault();
            if (string.IsNullOrEmpty(claimedId) || !claimedId.StartsWith(SteamClaimedIdPrefix, StringComparison.Ordinal))
            {
                logger.LogWarning("Steam callback: missing or invalid openid.claimed_id for user {UserId}", userIdStr);
                return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=missing_identity");
            }

            var steam64Id = claimedId[SteamClaimedIdPrefix.Length..];
            if (string.IsNullOrEmpty(steam64Id) || !long.TryParse(steam64Id, out _))
            {
                logger.LogWarning("Steam callback: invalid steam64 ID '{Steam64Id}' for user {UserId}", steam64Id, userIdStr);
                return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=invalid_steam_id");
            }

            // ── Verify OpenID response with Steam ────────────────────────────
            try
            {
                var isValid = await VerifySteamOpenIdResponse(ctx.Request.Query, http, ct);
                if (!isValid)
                {
                    logger.LogWarning("Steam callback: OpenID verification failed for user {UserId}, steam64 {Steam64Id}",
                        userIdStr, steam64Id);
                    return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=verification_failed");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Steam callback: OpenID verification threw for user {UserId}", userIdStr);
                return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=verification_failed");
            }

            // ── Guard: Steam64 ID already linked to another user ─────────────
            using var conn = db.CreateConnection();
            var existingOwner = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM public.player_steam_accounts WHERE steam64_id = @steam64Id AND user_id != @userId",
                new { steam64Id, userId });
            if (existingOwner is not null)
            {
                logger.LogWarning("Steam callback: steam64 {Steam64Id} already linked to user {ExistingUserId}, rejecting for {UserId}",
                    steam64Id, existingOwner, userIdStr);
                return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=already_linked_to_another_user");
            }

            // ── Fetch Steam player summary ───────────────────────────────────
            string? steamName  = null;
            string? avatarUrl  = null;
            string? profileUrl = null;

            var steamApiKey = config["Steam:ApiKey"];
            if (!string.IsNullOrEmpty(steamApiKey))
            {
                try
                {
                    var summaryUrl = $"{SteamPlayerSummaryUrl}?key={steamApiKey}&steamids={steam64Id}";
                    var summaryRes = await http.GetAsync(summaryUrl, ct);

                    if (summaryRes.IsSuccessStatusCode)
                    {
                        var body = await summaryRes.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(body);

                        var players = doc.RootElement
                            .GetProperty("response")
                            .GetProperty("players");

                        if (players.GetArrayLength() > 0)
                        {
                            var player = players[0];
                            steamName  = player.TryGetProperty("personaname", out var pn)  ? pn.GetString()  : null;
                            avatarUrl  = player.TryGetProperty("avatarfull",  out var av)  ? av.GetString()  : null;
                            profileUrl = player.TryGetProperty("profileurl",  out var pu)  ? pu.GetString()  : null;
                        }
                    }
                    else
                    {
                        logger.LogWarning("Steam callback: player summary API returned {StatusCode} for steam64 {Steam64Id}",
                            summaryRes.StatusCode, steam64Id);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Steam callback: failed to fetch player summary for steam64 {Steam64Id}", steam64Id);
                    // Non-fatal — continue with upsert, just without profile data
                }
            }
            else
            {
                logger.LogWarning("Steam callback: Steam:ApiKey not configured, skipping player summary fetch");
            }

            // ── Upsert into player_steam_accounts ────────────────────────────
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.player_steam_accounts
                        (user_id, steam64_id, steam_name, avatar_url, profile_url, linked_at, verified)
                    VALUES (@userId, @steam64Id, @steamName, @avatarUrl, @profileUrl, NOW(), TRUE)
                    ON CONFLICT (user_id) DO UPDATE SET
                        steam64_id  = EXCLUDED.steam64_id,
                        steam_name  = EXCLUDED.steam_name,
                        avatar_url  = EXCLUDED.avatar_url,
                        profile_url = EXCLUDED.profile_url,
                        linked_at   = NOW(),
                        verified    = TRUE
                    """, new { userId, steam64Id, steamName, avatarUrl, profileUrl });

                // Sync profiles.steam_tag with the linked Steam name
                if (!string.IsNullOrEmpty(steamName))
                {
                    await conn.ExecuteAsync(
                        "UPDATE public.profiles SET steam_tag = @steamName WHERE id = @userId",
                        new { steamName, userId });
                }

                logger.LogInformation("Steam account linked: user {UserId} ↔ steam64 {Steam64Id} ({SteamName})",
                    userIdStr, steam64Id, steamName);

                return Results.Redirect($"{frontendUrl}/account/settings?steam=linked");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Steam callback: upsert failed for user {UserId}, steam64 {Steam64Id}",
                    userIdStr, steam64Id);
                return Results.Redirect($"{frontendUrl}/account/settings?steam=error&reason=save_failed");
            }
        }); // Public — Steam redirect has no JWT

        // =====================================================================
        //  DELETE /api/accounts/steam — Unlink Steam account
        // =====================================================================
        app.MapDelete("/api/accounts/steam", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.player_steam_accounts WHERE user_id = @userId",
                new { userId = userCtx.UserIdGuid });

            // Clear profiles.steam_tag
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET steam_tag = NULL WHERE id = @userId",
                new { userId = userCtx.UserIdGuid });

            return Results.NoContent();
        }).RequireAuthorization("Authenticated");

        // =====================================================================
        //  GET /api/teams/{teamId}/steam-ids — Team roster Steam IDs
        // =====================================================================
        //  Used by MatchZy config generation to populate team configs.
        app.MapGet("/api/teams/{teamId}/steam-ids", async (
            Guid                 teamId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var members = await conn.QueryAsync<dynamic>(
                """
                SELECT tm.user_id,
                       psa.steam64_id,
                       psa.steam_name,
                       p.username AS gamer_tag
                FROM public.team_members tm
                INNER JOIN public.profiles p ON p.id = tm.user_id
                LEFT JOIN public.player_steam_accounts psa ON psa.user_id = tm.user_id
                WHERE tm.team_id = @teamId
                  AND tm.is_active = TRUE
                ORDER BY tm.display_order, p.username
                """, new { teamId });

            var result = members.Select(m => new
            {
                userId   = (Guid)m.user_id,
                steam64Id = (string?)m.steam64_id,
                steamName = (string?)m.steam_name,
                gamerTag  = (string?)m.gamer_tag
            });

            return Results.Ok(result);
        }).RequireAuthorization("Authenticated");
    }

    // =====================================================================
    //  Private helpers
    // =====================================================================

    /// <summary>
    /// Verifies a Steam OpenID 2.0 response by sending it back to Steam
    /// with openid.mode = check_authentication.
    /// </summary>
    private static async Task<bool> VerifySteamOpenIdResponse(
        IQueryCollection query,
        HttpClient       http,
        CancellationToken ct)
    {
        // Build verification form — copy all openid.* params,
        // but override mode to check_authentication
        var verifyParams = new Dictionary<string, string>();

        foreach (var key in query.Keys)
        {
            if (!key.StartsWith("openid.", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = query[key].FirstOrDefault();
            if (value is null) continue;

            verifyParams[key] = key == "openid.mode"
                ? "check_authentication"
                : value;
        }

        // Ensure required params are present
        if (!verifyParams.ContainsKey("openid.signed") ||
            !verifyParams.ContainsKey("openid.sig"))
            return false;

        var content  = new FormUrlEncodedContent(verifyParams);
        var response = await http.PostAsync(SteamOpenIdEndpoint, content, ct);

        if (!response.IsSuccessStatusCode)
            return false;

        var body = await response.Content.ReadAsStringAsync(ct);

        // Steam returns a key-value response; look for is_valid:true
        return body.Contains("is_valid:true", StringComparison.OrdinalIgnoreCase);
    }
}
