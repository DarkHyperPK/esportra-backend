using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

public sealed class SeasonPublishingService(
    IDbConnectionFactory db,
    GameCatalogService gameCatalog,
    SeasonValidationService validation,
    ILogger<SeasonPublishingService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SeasonPublishResult> PublishAsync(Guid seasonId, UserContext userCtx, HybridCache cache, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, seasonId, userCtx))
            throw new UnauthorizedAccessException("You do not have permission to publish this season.");

        using var tx = conn.BeginTransaction();
        var season = await conn.QuerySingleOrDefaultAsync<SeasonPublishSeasonRow>(
            """
            SELECT id, name, slug, game, game_mode AS gameMode, participant_mode AS participantMode,
                   region, catalog_game_slug AS catalogGameSlug, organization_id AS organizationId,
                   owner_user_id AS ownerUserId, banner_url AS bannerUrl, logo_url AS logoUrl, status
            FROM public.seasons
            WHERE id = @seasonId AND deleted_at IS NULL
            FOR UPDATE
            """,
            new { seasonId }, tx);
        if (season is null)
        {
            tx.Rollback();
            return new SeasonPublishResult(false, "not_found", [], [], 0, 0, 0);
        }

        GameModeCatalogResolution catalog;
        try
        {
            catalog = await gameCatalog.ResolveGameModeAsync(season.Game, season.GameMode, null, conn, tx);
        }
        catch (GameCatalogValidationException ex)
        {
            tx.Rollback();
            return new SeasonPublishResult(false, "validation_failed", [ex.Message], [], 0, 0, 0);
        }

        await conn.ExecuteAsync(
            """
            UPDATE public.seasons
            SET game = @game,
                game_mode = @gameMode,
                catalog_game_slug = @gameSlug,
                participant_mode = @participantMode,
                updated_at = NOW(),
                version = version + 1
            WHERE id = @seasonId
            """,
            new
            {
                seasonId,
                game = catalog.GameName,
                gameMode = catalog.GameMode,
                gameSlug = catalog.GameSlug,
                participantMode = catalog.ParticipantMode
            }, tx);

        var validationResult = await validation.ValidatePlanAsync(conn, tx, seasonId, ct);
        if (!validationResult.IsValid)
        {
            tx.Rollback();
            return new SeasonPublishResult(false, "validation_failed", validationResult.Issues, validationResult.Warnings, 0, 0, 0);
        }

        var nodes = (await conn.QueryAsync<SeasonPublishNodeRow>(
            """
            SELECT id, parent_node_id AS parentNodeId, name, slug, node_type AS nodeType, display_order AS displayOrder,
                   linked_tournament_id AS linkedTournamentId, starts_at AS startsAt, ends_at AS endsAt,
                   registration_deadline AS registrationDeadline, metadata::text AS metadata
            FROM public.season_nodes
            WHERE season_id = @seasonId AND node_type <> 'root'
            ORDER BY display_order ASC, created_at ASC
            """,
            new { seasonId }, tx)).AsList();

        var created = 0;
        var linked = 0;
        var nodeTournamentMap = new Dictionary<Guid, Guid>();

        foreach (var node in nodes)
        {
            var metadata = SeasonValidationService.ParseMetadata(node.Metadata);
            var structure = SeasonValidationService.GetString(metadata, "tournamentStructure")
                ?? SeasonValidationService.GetString(metadata, "format")
                ?? null;
            var resolved = await gameCatalog.ResolveTournamentAsync(
                catalog.GameName,
                catalog.GameMode,
                catalog.TeamSize,
                structure,
                null,
                Array.Empty<string>(),
                false,
                null,
                conn,
                tx);

            var role = SeasonValidationService.NormalizeSeasonTournamentRole(node.NodeType);
            var maxTeams = SeasonValidationService.GetInt(metadata, "maxTeams") ?? 16;
            var minTeams = Math.Min(2, maxTeams);
            var registrationType = SeasonValidationService.IsInboundOnlyNode(node.NodeType)
                ? "closed"
                : SeasonValidationService.GetString(metadata, "registrationType") ?? "open";
            var registrationPolicy = SeasonValidationService.IsInboundOnlyNode(node.NodeType)
                ? "inbound_only"
                : SeasonValidationService.GetString(metadata, "registrationPolicy") ?? "single_intake";
            var startDate = node.StartsAt ?? DateTime.UtcNow.AddDays(14 + Math.Max(node.DisplayOrder, 0));
            var endDate = node.EndsAt ?? startDate.AddHours(2);
            var registrationDeadline = SeasonValidationService.IsInboundOnlyNode(node.NodeType)
                ? (DateTime?)null
                : node.RegistrationDeadline ?? startDate.AddDays(-1);

            Guid tournamentId;
            if (node.LinkedTournamentId.HasValue)
            {
                tournamentId = node.LinkedTournamentId.Value;
                linked++;
            }
            else
            {
                var tournamentName = $"{season.Name} - {node.Name}";
                var slug = await CreateUniqueTournamentSlugAsync(conn, tx, tournamentName);
                var settings = JsonSerializer.Serialize(new
                {
                    seasonId,
                    seasonNodeId = node.Id,
                    seasonRole = role,
                    registrationType,
                    registrationPolicy,
                    generatedBySeason = true
                }, JsonOptions);

                tournamentId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO public.tournaments (
                        name, description, slug, game, game_mode, format, max_teams, min_teams, team_size,
                        entry_fee, prize_pool, start_date, end_date, registration_deadline,
                        status, organization_id, is_public, organizer_id, settings, region, banner_url, logo_url, currency
                    ) VALUES (
                        @name, @description, @slug, @game, @gameMode, @format, @maxTeams, @minTeams, @teamSize,
                        0, 0, @startDate, @endDate, @registrationDeadline,
                        'open'::tournament_status, @organizationId, TRUE, @organizerId, @settings::jsonb, @region, @bannerUrl, @logoUrl, 'USD'
                    )
                    RETURNING id
                    """,
                    new
                    {
                        name = tournamentName,
                        description = $"Generated {node.NodeType} for {season.Name}.",
                        slug,
                        game = resolved.GameName,
                        gameMode = resolved.GameMode,
                        format = resolved.TournamentStructure,
                        maxTeams,
                        minTeams,
                        teamSize = resolved.TeamSize,
                        startDate,
                        endDate,
                        registrationDeadline,
                        organizationId = season.OrganizationId,
                        organizerId = userCtx.UserIdGuid,
                        settings,
                        region = season.Region,
                        bannerUrl = season.BannerUrl,
                        logoUrl = season.LogoUrl
                    }, tx);
                created++;
            }

            nodeTournamentMap[node.Id] = tournamentId;
            await conn.ExecuteAsync(
                """
                UPDATE public.season_nodes
                SET linked_tournament_id = @tournamentId,
                    generated_tournament_id = COALESCE(generated_tournament_id, CASE WHEN @wasGenerated THEN @tournamentId ELSE NULL END),
                    status = CASE WHEN status = 'draft' THEN 'scheduled' ELSE status END,
                    metadata = jsonb_set(
                        jsonb_set(
                            jsonb_set(COALESCE(metadata, '{}'::jsonb), '{tournamentStructure}', to_jsonb(@structure::text), true),
                            '{registrationType}', to_jsonb(@registrationType::text), true
                        ),
                        '{registrationPolicy}', to_jsonb(@registrationPolicy::text), true
                    ),
                    updated_at = NOW()
                WHERE id = @nodeId
                """,
                new
                {
                    nodeId = node.Id,
                    tournamentId,
                    wasGenerated = !node.LinkedTournamentId.HasValue,
                    structure = resolved.TournamentStructure,
                    registrationType,
                    registrationPolicy
                }, tx);

            await conn.ExecuteAsync(
                """
                INSERT INTO public.season_tournaments (season_id, tournament_id, season_role, season_stage_order)
                VALUES (@seasonId, @tournamentId, @role, @stageOrder)
                ON CONFLICT (season_id, tournament_id) DO UPDATE SET
                    season_role = EXCLUDED.season_role,
                    season_stage_order = EXCLUDED.season_stage_order
                """,
                new { seasonId, tournamentId, role, stageOrder = node.DisplayOrder }, tx);
        }

        await conn.ExecuteAsync("DELETE FROM public.season_advancement_rules WHERE season_id = @seasonId", new { seasonId }, tx);
        var ruleCount = 0;
        foreach (var source in nodes)
        {
            var sourceTournamentId = nodeTournamentMap.GetValueOrDefault(source.Id);
            if (sourceTournamentId == Guid.Empty) continue;

            var metadata = SeasonValidationService.ParseMetadata(source.Metadata);
            foreach (var connection in SeasonValidationService.ReadConnections(source.Id, metadata))
            {
                if (!nodeTournamentMap.TryGetValue(connection.TargetNodeId, out var targetTournamentId)) continue;
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_advancement_rules
                        (season_id, source_node_id, target_node_id, source_tournament_id, target_tournament_id,
                         placement_start, placement_end, advancement_count, seed_mode)
                    VALUES
                        (@seasonId, @sourceNodeId, @targetNodeId, @sourceTournamentId, @targetTournamentId,
                         1, @advanceTeams, @advanceTeams, 'top_seeded')
                    """,
                    new
                    {
                        seasonId,
                        sourceNodeId = source.Id,
                        targetNodeId = connection.TargetNodeId,
                        sourceTournamentId,
                        targetTournamentId,
                        advanceTeams = connection.AdvanceTeams
                    }, tx);
                ruleCount++;
            }
        }

        var published = await conn.QuerySingleAsync<SeasonPublishStatusRow>(
            """
            UPDATE public.seasons
            SET status = 'published', is_public = TRUE, updated_at = NOW(), version = version + 1
            WHERE id = @seasonId
            RETURNING id, status
            """,
            new { seasonId }, tx);

        tx.Commit();
        await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, seasonId, ct);
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { }

        logger.LogInformation("Published season {SeasonId}: {Created} generated tournaments, {Linked} linked tournaments, {Rules} advancement rules.",
            seasonId, created, linked, ruleCount);

        return new SeasonPublishResult(true, published.Status, [], validationResult.Warnings, created, linked, ruleCount);
    }

    private static async Task<string> CreateUniqueTournamentSlugAsync(IDbConnection conn, IDbTransaction tx, string name)
    {
        var slug = SeasonEndpointHelpers.Slugify(name);
        var unique = slug;
        var suffix = 1;
        while (suffix < 1000 && await conn.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.tournaments WHERE LOWER(slug) = LOWER(@slug))",
            new { slug = unique }, tx))
        {
            suffix++;
            unique = $"{slug}-{suffix}";
        }
        if (suffix >= 1000) throw new InvalidOperationException("Unable to generate a unique tournament slug.");
        return unique;
    }

    private sealed record SeasonPublishSeasonRow(
        Guid Id,
        string Name,
        string Slug,
        string Game,
        string? GameMode,
        string ParticipantMode,
        string? Region,
        string? CatalogGameSlug,
        Guid? OrganizationId,
        Guid OwnerUserId,
        string? BannerUrl,
        string? LogoUrl,
        string Status);

    private sealed record SeasonPublishNodeRow(
        Guid Id,
        Guid? ParentNodeId,
        string Name,
        string? Slug,
        string NodeType,
        int DisplayOrder,
        Guid? LinkedTournamentId,
        DateTime? StartsAt,
        DateTime? EndsAt,
        DateTime? RegistrationDeadline,
        string? Metadata);

    private sealed record SeasonPublishStatusRow(Guid Id, string Status);
}

public sealed record SeasonPublishResult(
    bool Success,
    string Status,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings,
    int CreatedTournaments,
    int LinkedTournaments,
    int AdvancementRules);
