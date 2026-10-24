using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Competitive player profiles (PROJ-065 Phase 1).
/// GET  /api/competitive/profiles/search      — discover visible profiles
/// GET  /api/competitive/profiles/{userId}    — get one profile (?game= required)
/// PUT  /api/competitive/profiles             — own profile upsert
/// </summary>
public static class CompetitiveProfileEndpoints
{
    public static void MapCompetitiveProfileEndpoints(this WebApplication app)
    {
        // Search registered first — literal "search" wins over {userId:guid} automatically,
        // but explicit ordering makes intent clear.
        app.MapGet("/api/competitive/profiles/search", SearchProfilesAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/profiles/{userId}", GetProfileAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/competitive/profiles", UpsertProfileAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/competitive/profiles/{userId}?game= ─────────────────────────

    private static async Task<IResult> GetProfileAsync(
        Guid userId,
        [FromQuery] string? game,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        var callerCtx = ctx.Items["UserContext"] as UserContext;
        var isOwner = callerCtx?.UserIdGuid == userId;

        using var conn = db.CreateConnection();

        var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT pgp.*,
                   ra.puuid      AS riot_puuid,
                   ra.game_name  AS riot_gamename,
                   ra.tag_line   AS riot_tagline
            FROM public.player_game_profiles pgp
            LEFT JOIN LATERAL (
                SELECT puuid, game_name, tag_line
                FROM public.riot_accounts
                WHERE user_id = pgp.user_id
                ORDER BY updated_at DESC
                LIMIT 1
            ) ra ON (pgp.riot_id_visible OR @isOwner)
            WHERE pgp.user_id = @userId
              AND pgp.game_slug = @game
              AND (pgp.profile_visible = TRUE OR @isOwner)
            """,
            new { userId, game, isOwner });

        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }

    // ── GET /api/competitive/profiles/search ─────────────────────────────────

    private static async Task<IResult> SearchProfilesAsync(
        [FromQuery] string? game,
        [FromQuery] bool? looking_for_team,
        [FromQuery] string? region_key,
        [FromQuery] string? preferred_roles,
        [FromQuery] int? min_rank,
        [FromQuery] int? max_rank,
        [FromQuery] int limit,
        [FromQuery] int offset,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 50);
        offset = Math.Max(offset, 0);

        var conditions = new List<string>
        {
            "pgp.game_slug = @game",
            "pgp.profile_visible = TRUE",
        };
        var p = new DynamicParameters();
        p.Add("game", game);
        p.Add("limit", limit);
        p.Add("offset", offset);

        if (looking_for_team.HasValue)
        {
            conditions.Add("pgp.looking_for_team = @lft");
            p.Add("lft", looking_for_team.Value);
        }

        if (!string.IsNullOrWhiteSpace(region_key))
        {
            conditions.Add("pgp.region_key = @regionKey");
            p.Add("regionKey", region_key);
        }

        if (!string.IsNullOrWhiteSpace(preferred_roles))
        {
            var roles = preferred_roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (roles.Length > 0)
            {
                conditions.Add("@roles::text[] && pgp.preferred_roles");
                p.Add("roles", roles);
            }
        }

        if (min_rank.HasValue)
        {
            conditions.Add("grt.sort_order >= @minRank");
            p.Add("minRank", min_rank.Value);
        }

        if (max_rank.HasValue)
        {
            conditions.Add("grt.sort_order <= @maxRank");
            p.Add("maxRank", max_rank.Value);
        }

        var where = string.Join(" AND ", conditions);

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            $"""
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT pgp.id, pgp.user_id, pgp.game_slug, pgp.rank_tier_key,
                   pgp.preferred_roles, pgp.main_role, pgp.region_key,
                   pgp.looking_for_team, pgp.recruitment_visible,
                   pgp.competitive_experience, pgp.preferred_team_environment,
                   pgp.languages, pgp.updated_at,
                   pr.username, pr.avatar_seed,
                   grt.sort_order AS rank_sort_order
            FROM public.player_game_profiles pgp
            JOIN public.profiles pr ON pr.id = pgp.user_id
            LEFT JOIN public.game_catalog_rank_tiers grt
                ON grt.game_slug = pgp.game_slug
               AND grt.tier_key = pgp.rank_tier_key
               AND grt.version_id = (SELECT id FROM active_v)
            WHERE {where}
            ORDER BY pgp.looking_for_team DESC, grt.sort_order DESC NULLS LAST, pr.username ASC
            LIMIT @limit OFFSET @offset
            """,
            p);

        return Results.Ok(rows);
    }

    // ── PUT /api/competitive/profiles ─────────────────────────────────────────

    private static async Task<IResult> UpsertProfileAsync(
        [FromBody] UpsertGameProfileRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.GameSlug))
            return Results.BadRequest(new { error = "game_slug is required." });

        using var conn = db.CreateConnection();

        var validationError = await ValidateGameProfileFieldsAsync(conn, req);
        if (validationError is not null) return validationError;

        var preferredRoles = req.PreferredRoles ?? Array.Empty<string>();
        var preferredServers = req.PreferredServers ?? Array.Empty<string>();
        var languages = req.Languages ?? Array.Empty<string>();

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            INSERT INTO public.player_game_profiles
                (user_id, game_slug, rank_tier_key, rank_source,
                 preferred_roles, main_role, region_key, preferred_servers,
                 looking_for_team, recruitment_visible, profile_visible,
                 availability_visible, riot_id_visible,
                 competitive_experience, preferred_team_environment,
                 languages, notes, updated_at)
            VALUES
                (@userId, @gameSlug, @rankTierKey, COALESCE(@rankSource, 'self_reported'),
                 @preferredRoles, @mainRole, @regionKey, @preferredServers,
                 COALESCE(@lookingForTeam, FALSE), COALESCE(@recruitmentVisible, FALSE),
                 COALESCE(@profileVisible, FALSE), COALESCE(@availabilityVisible, FALSE),
                 COALESCE(@riotIdVisible, FALSE),
                 @competitiveExperience, @preferredTeamEnvironment,
                 @languages, @notes, now())
            ON CONFLICT (user_id, game_slug) DO UPDATE SET
                rank_tier_key              = COALESCE(@rankTierKey, player_game_profiles.rank_tier_key),
                rank_source                = COALESCE(@rankSource, player_game_profiles.rank_source),
                preferred_roles            = @preferredRoles,
                main_role                  = COALESCE(@mainRole, player_game_profiles.main_role),
                region_key                 = COALESCE(@regionKey, player_game_profiles.region_key),
                preferred_servers          = @preferredServers,
                looking_for_team           = COALESCE(@lookingForTeam, player_game_profiles.looking_for_team),
                recruitment_visible        = COALESCE(@recruitmentVisible, player_game_profiles.recruitment_visible),
                profile_visible            = COALESCE(@profileVisible, player_game_profiles.profile_visible),
                availability_visible       = COALESCE(@availabilityVisible, player_game_profiles.availability_visible),
                riot_id_visible            = COALESCE(@riotIdVisible, player_game_profiles.riot_id_visible),
                competitive_experience     = COALESCE(@competitiveExperience, player_game_profiles.competitive_experience),
                preferred_team_environment = COALESCE(@preferredTeamEnvironment, player_game_profiles.preferred_team_environment),
                languages                  = @languages,
                notes                      = COALESCE(@notes, player_game_profiles.notes),
                updated_at                 = now()
            RETURNING *
            """,
            new
            {
                userId = userCtx.UserIdGuid,
                gameSlug = req.GameSlug,
                rankTierKey = req.RankTierKey,
                rankSource = req.RankSource,
                preferredRoles,
                mainRole = req.MainRole,
                regionKey = req.RegionKey,
                preferredServers,
                lookingForTeam = req.LookingForTeam,
                recruitmentVisible = req.RecruitmentVisible,
                profileVisible = req.ProfileVisible,
                availabilityVisible = req.AvailabilityVisible,
                riotIdVisible = req.RiotIdVisible,
                competitiveExperience = req.CompetitiveExperience,
                preferredTeamEnvironment = req.PreferredTeamEnvironment,
                languages,
                notes = req.Notes,
            });

        return Results.Ok(row);
    }

    // ── Validation helpers ─────────────────────────────────────────────────────

    private static async Task<IResult?> ValidateGameProfileFieldsAsync(
        System.Data.IDbConnection conn,
        UpsertGameProfileRequest req)
    {
        var gameExists = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.game_catalog_game_aliases a
                JOIN public.game_catalog_versions v ON v.id = a.version_id
                WHERE v.is_active = TRUE AND LOWER(a.alias) = LOWER(@game)
            )
            """,
            new { game = req.GameSlug });

        if (!gameExists)
            return Results.BadRequest(new { error = "Unknown game." });

        if (req.RankTierKey is not null)
        {
            var rankValid = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM public.game_catalog_rank_tiers rt
                    JOIN public.game_catalog_versions v ON v.id = rt.version_id
                    WHERE v.is_active = TRUE AND rt.game_slug = @game AND rt.tier_key = @rank
                )
                """,
                new { game = req.GameSlug, rank = req.RankTierKey });
            if (!rankValid)
                return Results.BadRequest(new { error = "Invalid rank_tier_key for this game." });
        }

        if (req.PreferredRoles is { Length: > 0 })
        {
            var validRoleKeys = (await conn.QueryAsync<string>(
                """
                SELECT role_key FROM public.game_catalog_game_roles gr
                JOIN public.game_catalog_versions v ON v.id = gr.version_id
                WHERE v.is_active = TRUE AND gr.game_slug = @game
                """,
                new { game = req.GameSlug })).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var bad = req.PreferredRoles.FirstOrDefault(r => !validRoleKeys.Contains(r));
            if (bad is not null)
                return Results.BadRequest(new { error = $"Unknown role '{bad}' for this game." });
        }

        if (req.RegionKey is not null)
        {
            var regionValid = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM public.game_catalog_regions r
                    JOIN public.game_catalog_versions v ON v.id = r.version_id
                    WHERE v.is_active = TRUE AND r.game_slug = @game AND r.region_key = @region
                )
                """,
                new { game = req.GameSlug, region = req.RegionKey });
            if (!regionValid)
                return Results.BadRequest(new { error = "Invalid region_key for this game." });
        }

        // rank_source must be self_reported in Phase 1 (provider_linked requires Riot Tournament API)
        if (req.RankSource is not null &&
            !string.Equals(req.RankSource, "self_reported", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "rank_source must be 'self_reported' in Phase 1." });

        return null;
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record UpsertGameProfileRequest(
    [property: JsonPropertyName("game_slug")] string GameSlug,
    [property: JsonPropertyName("rank_tier_key")] string? RankTierKey = null,
    [property: JsonPropertyName("rank_source")] string? RankSource = null,
    [property: JsonPropertyName("preferred_roles")] string[]? PreferredRoles = null,
    [property: JsonPropertyName("main_role")] string? MainRole = null,
    [property: JsonPropertyName("region_key")] string? RegionKey = null,
    [property: JsonPropertyName("preferred_servers")] string[]? PreferredServers = null,
    [property: JsonPropertyName("looking_for_team")] bool? LookingForTeam = null,
    [property: JsonPropertyName("recruitment_visible")] bool? RecruitmentVisible = null,
    [property: JsonPropertyName("profile_visible")] bool? ProfileVisible = null,
    [property: JsonPropertyName("availability_visible")] bool? AvailabilityVisible = null,
    [property: JsonPropertyName("riot_id_visible")] bool? RiotIdVisible = null,
    [property: JsonPropertyName("competitive_experience")] string? CompetitiveExperience = null,
    [property: JsonPropertyName("preferred_team_environment")] string? PreferredTeamEnvironment = null,
    [property: JsonPropertyName("languages")] string[]? Languages = null,
    [property: JsonPropertyName("notes")] string? Notes = null);
