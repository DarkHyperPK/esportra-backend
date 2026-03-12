using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: riot-match-proxy, faceit-match-proxy, riot-oauth, faceit-oauth Edge Functions.
/// </summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        // ── GET /api/integrations/riot ────────────────────────────────────────
        // Returns the current user's linked Riot account (if any).
        app.MapGet("/api/integrations/riot", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();
            if (!Guid.TryParse(userId, out var userGuid)) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var account = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ra.puuid, ra.game_name, ra.tag_line, ra.region, ra.linked_at,
                       p.riot_tag
                FROM public.riot_accounts ra
                JOIN public.profiles p ON p.id = ra.user_id
                WHERE ra.user_id = @userId
                """, new { userId = userGuid });

            if (account is null) return Results.Ok(new { linked = false });
            return Results.Ok(new
            {
                linked   = true,
                puuid    = (string?)account.puuid,
                gameName = (string?)account.game_name,
                tagLine  = (string?)account.tag_line,
                region   = (string?)account.region,
                riotTag  = (string?)account.riot_tag
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/integrations/faceit ──────────────────────────────────────
        // Returns the current user's linked FACEIT account (if any).
        app.MapGet("/api/integrations/faceit", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();
            if (!Guid.TryParse(userId, out var userGuid)) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var account = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT fa.faceit_id, fa.nickname, fa.avatar_url, fa.linked_at,
                       p.faceit_nickname
                FROM public.faceit_accounts fa
                JOIN public.profiles p ON p.id = fa.user_id
                WHERE fa.user_id = @userId
                """, new { userId = userGuid });

            if (account is null) return Results.Ok(new { linked = false });
            return Results.Ok(new
            {
                linked        = true,
                faceitId      = (string?)account.faceit_id,
                nickname      = (string?)account.nickname,
                avatarUrl     = (string?)account.avatar_url,
                faceitNickname = (string?)account.faceit_nickname
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/integrations/riot/proxy ─────────────────────────────────
        // Replaces: riot-match-proxy Edge Function
        app.MapPost("/api/integrations/riot/proxy", async (
            [FromBody] RiotProxyRequest req,
            RiotApiClient               riot,
            CancellationToken           ct) =>
        {
            var (status, body) = await riot.ProxyAsync(req.Region, req.Endpoint, ct);
            return Results.Content(body, "application/json", statusCode: status);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/integrations/faceit/proxy ───────────────────────────────
        // Replaces: faceit-match-proxy Edge Function
        app.MapPost("/api/integrations/faceit/proxy", async (
            [FromBody] FaceitProxyRequest req,
            FaceitApiClient               faceit,
            CancellationToken             ct) =>
        {
            var (status, body) = await faceit.ProxyAsync(req.Endpoint, ct);
            return Results.Content(body, "application/json", statusCode: status);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/integrations/riot/callback ──────────────────────────────
        // Replaces: riot-oauth Edge Function (token exchange step)
        // The redirect step (GET) is handled client-side — only the POST exchange is server-side.
        app.MapPost("/api/integrations/riot/callback", async (
            [FromBody] RiotOAuthCallbackRequest req,
            IDbConnectionFactory                db,
            IConfiguration                      config,
            HttpContext                         ctx,
            HttpClient                          http,
            CancellationToken                   ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            var clientId     = config["Riot:OAuthClientId"]     ?? string.Empty;
            var clientSecret = config["Riot:OAuthClientSecret"] ?? string.Empty;

            // Exchange code for tokens
            var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://auth.riotgames.com/token");
            tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["code"]          = req.Code,
                ["redirect_uri"]  = req.RedirectUri,
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
            });

            var tokenRes = await http.SendAsync(tokenReq, ct);
            if (!tokenRes.IsSuccessStatusCode)
                return Results.BadRequest(new { error = "Token exchange failed." });

            var tokenBody = await tokenRes.Content.ReadAsStringAsync(ct);
            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var accessToken  = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
            var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() : null;
            var expiresIn    = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei)
                ? ei.GetInt32() : 3600;
            var expiresAt    = DateTime.UtcNow.AddSeconds(expiresIn);

            // Fetch account info
            var infoReq = new HttpRequestMessage(HttpMethod.Get,
                "https://asia.api.riotgames.com/riot/account/v1/accounts/me");
            infoReq.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var infoRes = await http.SendAsync(infoReq, ct);
            if (!infoRes.IsSuccessStatusCode)
                return Results.BadRequest(new { error = "Failed to fetch Riot account info." });

            var infoBody = await infoRes.Content.ReadAsStringAsync(ct);
            using var infoDoc = JsonDocument.Parse(infoBody);
            var puuid    = infoDoc.RootElement.GetProperty("puuid").GetString()!;
            var gameName = infoDoc.RootElement.GetProperty("gameName").GetString()!;
            var tagLine  = infoDoc.RootElement.GetProperty("tagLine").GetString()!;

            using var conn = db.CreateConnection();

            // Guard: check if PUUID already linked to another account
            var existingUserId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM public.riot_accounts WHERE puuid = @puuid AND user_id != @userId",
                new { puuid, userId });
            if (existingUserId is not null)
                return Results.Conflict(new { error = "This Riot account is already linked to another user." });

            // Upsert riot_accounts
            await conn.ExecuteAsync("""
                INSERT INTO public.riot_accounts
                    (user_id, puuid, game_name, tag_line, access_token, refresh_token, token_expires_at)
                VALUES (@userId, @puuid, @gameName, @tagLine, @accessToken, @refreshToken, @expiresAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    puuid = EXCLUDED.puuid,
                    game_name = EXCLUDED.game_name,
                    tag_line = EXCLUDED.tag_line,
                    access_token = EXCLUDED.access_token,
                    refresh_token = EXCLUDED.refresh_token,
                    token_expires_at = EXCLUDED.token_expires_at
                """, new { userId, puuid, gameName, tagLine, accessToken, refreshToken, expiresAt });

            // Sync profiles.riot_tag
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET riot_tag = @riotTag WHERE id = @userId",
                new { riotTag = $"{gameName}#{tagLine}", userId });

            return Results.Ok(new { success = true, gameName, tagLine });

        }).RequireAuthorization("Authenticated");

        // ── POST /api/integrations/faceit/callback ────────────────────────────
        // Replaces: faceit-oauth Edge Function (PKCE token exchange)
        app.MapPost("/api/integrations/faceit/callback", async (
            [FromBody] FaceitOAuthCallbackRequest req,
            IDbConnectionFactory                  db,
            FaceitApiClient                       faceit,
            HttpContext                           ctx,
            HttpClient                            http,
            CancellationToken                     ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            var tokens = await faceit.ExchangeCodeAsync(
                req.Code, req.CodeVerifier, req.RedirectUri, ct);

            if (tokens is null)
                return Results.BadRequest(new { error = "FACEIT token exchange failed." });

            // Fetch account info via userinfo endpoint
            var infoReq = new HttpRequestMessage(HttpMethod.Get,
                "https://api.faceit.com/auth/v1/resources/userinfo");
            infoReq.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.access_token);

            var infoRes = await http.SendAsync(infoReq, ct);
            if (!infoRes.IsSuccessStatusCode)
                return Results.BadRequest(new { error = "Failed to fetch FACEIT account info." });

            var infoBody = await infoRes.Content.ReadAsStringAsync(ct);
            using var doc  = JsonDocument.Parse(infoBody);
            var root       = doc.RootElement;
            var faceitId   = root.GetProperty("sub").GetString()!;
            var nickname   = root.TryGetProperty("nickname", out var nn) ? nn.GetString() : null;
            var avatarUrl  = root.TryGetProperty("picture",  out var av) ? av.GetString() : null;
            var expiresAt  = DateTime.UtcNow.AddSeconds(tokens.expires_in);

            using var conn = db.CreateConnection();

            // Guard duplicates
            var existingUserId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM public.faceit_accounts WHERE faceit_id = @faceitId AND user_id != @userId",
                new { faceitId, userId });
            if (existingUserId is not null)
                return Results.Conflict(new { error = "This FACEIT account is already linked to another user." });

            await conn.ExecuteAsync("""
                INSERT INTO public.faceit_accounts
                    (user_id, faceit_id, nickname, avatar_url, access_token, refresh_token, token_expires_at)
                VALUES (@userId, @faceitId, @nickname, @avatarUrl, @accessToken, @refreshToken, @expiresAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    faceit_id = EXCLUDED.faceit_id,
                    nickname = EXCLUDED.nickname,
                    avatar_url = EXCLUDED.avatar_url,
                    access_token = EXCLUDED.access_token,
                    refresh_token = EXCLUDED.refresh_token,
                    token_expires_at = EXCLUDED.token_expires_at
                """, new { userId, faceitId, nickname, avatarUrl,
                           accessToken = tokens.access_token,
                           refreshToken = tokens.refresh_token, expiresAt });

            await conn.ExecuteAsync(
                "UPDATE public.profiles SET faceit_nickname = @nickname WHERE id = @userId",
                new { nickname, userId });

            return Results.Ok(new { success = true, nickname, faceitId });

        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/integrations/riot ─────────────────────────────────────
        app.MapDelete("/api/integrations/riot", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.riot_accounts WHERE user_id = @userId", new { userId });
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET riot_tag = NULL WHERE id = @userId", new { userId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/integrations/faceit ───────────────────────────────────
        app.MapDelete("/api/integrations/faceit", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.faceit_accounts WHERE user_id = @userId", new { userId });
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET faceit_nickname = NULL WHERE id = @userId",
                new { userId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }
}
