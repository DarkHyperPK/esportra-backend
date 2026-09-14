using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Bracket;
using Esportra.Core.Match;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Developer API v1 — Partner/TO access via API key auth.
/// All endpoints require ApiKeyContext set by ApiKeyAuthMiddleware.
/// Scope checks are enforced at the handler boundary.
/// </summary>
public static class DeveloperV1Endpoints
{
    public static void MapDeveloperV1Endpoints(this WebApplication app)
    {
        MapTournamentEndpoints(app);
        MapParticipantEndpoints(app);
        MapBracketEndpoints(app);
        MapMatchEndpoints(app);
        MapVetoEndpoints(app);
    }

    // ── Tournaments ─────────────────────────────────────────────────────────────

    private static void MapTournamentEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/tournaments", CreateTournament)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapGet("/api/v1/tournaments/{id}", GetTournament)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapPatch("/api/v1/tournaments/{id}", PatchTournament)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapPost("/api/v1/tournaments/{id}/publish", PublishTournament)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");
    }

    // ── Participants ─────────────────────────────────────────────────────────────

    private static void MapParticipantEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/tournaments/{id}/participants", AddParticipant)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapGet("/api/v1/tournaments/{id}/participants", ListParticipants)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapDelete("/api/v1/tournaments/{id}/participants/{participantId}", RemoveParticipant)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");
    }

    // ── Brackets ─────────────────────────────────────────────────────────────────

    private static void MapBracketEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/tournaments/{id}/bracket/generate", GenerateBracket)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapPost("/api/v1/tournaments/{id}/bracket/seed", SeedBracket)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapGet("/api/v1/tournaments/{id}/bracket", GetBracket)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapGet("/api/v1/tournaments/{id}/standings", GetStandings)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");
    }

    // ── Matches ──────────────────────────────────────────────────────────────────

    private static void MapMatchEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/tournaments/{id}/matches", ListMatches)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapGet("/api/v1/matches/{matchId}", GetMatch)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapPost("/api/v1/matches/{matchId}/result", ReportResult)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapPatch("/api/v1/matches/{matchId}/schedule", ScheduleMatch)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");
    }

    // ── Veto ─────────────────────────────────────────────────────────────────────

    private static void MapVetoEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/matches/{matchId}/veto", GetVeto)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");

        app.MapPost("/api/v1/matches/{matchId}/veto/pick", VetoPick)
            .RequireAuthorization("Authenticated").WithTags("Developer API v1");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Tournament Handlers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> CreateTournament(
        [FromBody] V1CreateTournamentRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsWrite)) return Results.Forbid();

        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name is required" });
        if (string.IsNullOrWhiteSpace(req.Game)) return Results.BadRequest(new { error = "game is required" });
        if (string.IsNullOrWhiteSpace(req.Format)) return Results.BadRequest(new { error = "format is required" });

        var settingsJson = apiCtx.IsSandbox ? """{"api_sandbox":true}""" : null;
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleAsync<dynamic>(
            """
            INSERT INTO tournaments (
                name, game, format, max_teams, start_date, end_date,
                status, organization_id, organizer_id, region, settings, is_public
            ) VALUES (
                @name, @game, @format, @maxTeams, @startDate, @endDate,
                'draft', @orgId, @ownerId, @region, @settings::jsonb, FALSE
            )
            RETURNING id, name, status::text AS status, created_at
            """,
            new
            {
                name = req.Name,
                game = req.Game,
                format = req.Format,
                maxTeams = req.MaxParticipants,
                startDate = req.StartDate,
                endDate = req.EndDate,
                orgId = apiCtx.OrgId,
                ownerId = apiCtx.OwnerId,
                region = req.Region,
                settings = settingsJson ?? "{}",
            });

        return Results.Created(
            $"/api/v1/tournaments/{row.id}",
            new { id = row.id, name = row.name, status = row.status, environment = apiCtx.Environment, created_at = row.created_at });
    }

    private static async Task<IResult> GetTournament(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsRead)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT t.id, t.name, t.game, t.format, t.status::text AS status, t.region,
                   t.max_teams, t.start_date, t.end_date, t.created_at,
                   (SELECT COUNT(*) FROM tournament_external_participants ep
                    WHERE ep.tournament_id = t.id AND ep.organization_id = @orgId) AS participant_count
            FROM tournaments t
            WHERE t.id = @id AND t.organization_id = @orgId
            """,
            new { id, orgId = apiCtx.OrgId });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    private static async Task<IResult> PatchTournament(
        Guid id,
        [FromBody] V1PatchTournamentRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsWrite)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Status)>(
            "SELECT id, status::text FROM tournaments WHERE id = @id AND organization_id = @orgId",
            new { id, orgId = apiCtx.OrgId });

        if (existing == default) return Results.NotFound();
        if (existing.Status != "draft" && existing.Status != "open")
            return Results.Conflict(new { error = "Tournament can only be updated in draft or open status." });

        await conn.ExecuteAsync(
            """
            UPDATE tournaments
            SET name       = COALESCE(@name, name),
                start_date = COALESCE(@startDate, start_date),
                end_date   = COALESCE(@endDate, end_date),
                max_teams  = COALESCE(@maxTeams, max_teams),
                region     = COALESCE(@region, region),
                updated_at = NOW()
            WHERE id = @id AND organization_id = @orgId
            """,
            new { id, orgId = apiCtx.OrgId, name = req.Name, startDate = req.StartDate, endDate = req.EndDate, maxTeams = req.MaxParticipants, region = req.Region });

        return Results.Ok(new { id });
    }

    private static async Task<IResult> PublishTournament(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsWrite)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE tournaments SET status = 'published', updated_at = NOW()
            WHERE id = @id AND organization_id = @orgId AND status::text IN ('draft', 'open')
            """,
            new { id, orgId = apiCtx.OrgId });

        if (affected == 0)
        {
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organization_id = @orgId)",
                new { id, orgId = apiCtx.OrgId });
            return exists
                ? Results.Conflict(new { error = "Tournament is already published." })
                : Results.NotFound();
        }

        return Results.Ok(new { id, status = "published" });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Participant Handlers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> AddParticipant(
        Guid id,
        [FromBody] V1AddParticipantRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsWrite)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.ExternalId)) return Results.BadRequest(new { error = "external_id is required" });

        var tournamentExists = await VerifyTournamentOrgAsync(db, id, apiCtx.OrgId);
        if (!tournamentExists) return Results.NotFound();

        using var conn = db.CreateConnection();
        try
        {
            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_external_participants
                    (tournament_id, organization_id, external_id, name, metadata, seeding)
                VALUES (@tournamentId, @orgId, @externalId, @name, @metadata::jsonb, @seeding)
                RETURNING id, external_id, name, seeding, created_at
                """,
                new
                {
                    tournamentId = id,
                    orgId = apiCtx.OrgId,
                    externalId = req.ExternalId,
                    name = req.Name ?? req.ExternalId,
                    metadata = req.Metadata is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.Metadata)
                        : "{}",
                    seeding = req.Seeding,
                });
            return Results.Created($"/api/v1/tournaments/{id}/participants/{row.id}", row);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Conflict(new { error = "A participant with this external_id already exists." });
        }
    }

    private static async Task<IResult> ListParticipants(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsRead)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, external_id, name, seeding, placement
            FROM tournament_external_participants
            WHERE tournament_id = @id AND organization_id = @orgId
            ORDER BY seeding NULLS LAST, created_at ASC
            """,
            new { id, orgId = apiCtx.OrgId });

        return Results.Ok(new { participants = rows });
    }

    private static async Task<IResult> RemoveParticipant(
        Guid id,
        Guid participantId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.TournamentsWrite)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM tournament_external_participants WHERE id = @participantId AND tournament_id = @id AND organization_id = @orgId",
            new { participantId, id, orgId = apiCtx.OrgId });

        return affected == 0 ? Results.NotFound() : Results.NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Bracket Handlers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GenerateBracket(
        Guid id,
        [FromBody] V1BracketGenerateRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        BracketPersistenceService persistence,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.BracketsWrite)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var stageRow = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Format)>(
            "SELECT s.id, s.format FROM tournament_stages s JOIN tournaments t ON t.id = s.tournament_id WHERE s.id = @stageId AND t.id = @id AND t.organization_id = @orgId",
            new { stageId = req.StageId, id, orgId = apiCtx.OrgId });

        if (stageRow == default) return Results.NotFound();

        var participants = (await conn.QueryAsync<(Guid Id, string Name)>(
            "SELECT id, name FROM tournament_external_participants WHERE tournament_id = @id AND organization_id = @orgId ORDER BY seeding NULLS LAST",
            new { id, orgId = apiCtx.OrgId })).ToList();

        var format = req.Format ?? stageRow.Format;
        IBracketGenerator generator = format.ToLowerInvariant() switch
        {
            "double_elimination" => new DoubleEliminationGenerator(),
            "round_robin" => new RoundRobinGenerator(),
            "swiss" => new SwissGenerator(),
            _ => new SingleEliminationGenerator(),
        };

        var roundConfig = new StageRoundConfiguration(format, req.BestOf, "per_stage", null);
        var graph = generator.Generate(participants, id, req.StageId, roundConfig, req.BracketSize, null, null);
        var version = await persistence.SaveGraphAsync(graph, ct);

        return Results.Created(
            $"/api/v1/tournaments/{id}/bracket",
            new { bracket_version_id = version.Id, status = "generated", match_count = graph.Nodes.Count });
    }

    private static async Task<IResult> SeedBracket(
        Guid id,
        [FromBody] V1SeedBracketRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.BracketsWrite)) return Results.Forbid();

        if (req.Seeds is not { Count: > 0 }) return Results.BadRequest(new { error = "seeds must not be empty" });

        var tournamentExists = await VerifyTournamentOrgAsync(db, id, apiCtx.OrgId);
        if (!tournamentExists) return Results.NotFound();

        using var conn = db.CreateConnection();
        var seededCount = 0;
        foreach (var seed in req.Seeds)
        {
            var affected = await conn.ExecuteAsync(
                "UPDATE tournament_external_participants SET seeding = @position WHERE id = @participantId AND tournament_id = @id AND organization_id = @orgId",
                new { position = seed.Position, participantId = seed.ParticipantId, id, orgId = apiCtx.OrgId });
            seededCount += affected;
        }

        return Results.Ok(new { seeded_count = seededCount });
    }

    private static async Task<IResult> GetBracket(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.BracketsRead)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var versionRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT bv.id AS version_id, bv.status, bv.created_at
            FROM brkt_versions bv
            JOIN tournament_stages s ON s.id = bv.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE t.id = @id AND t.organization_id = @orgId
            ORDER BY bv.version_number DESC
            LIMIT 1
            """,
            new { id, orgId = apiCtx.OrgId });

        if (versionRow is null) return Results.NotFound();

        Guid versionId = (Guid)versionRow.version_id;
        var matches = await conn.QueryAsync<dynamic>(
            "SELECT id, round_index, match_number, bracket_type, status, best_of, team1_id, team2_id, winner_id, team1_score, team2_score FROM brkt_matches WHERE version_id = @versionId ORDER BY round_index, match_number",
            new { versionId });

        return Results.Ok(new { version = versionRow, matches });
    }

    private static async Task<IResult> GetStandings(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.BracketsRead)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var tournamentExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organization_id = @orgId)",
            new { id, orgId = apiCtx.OrgId });
        if (!tournamentExists) return Results.NotFound();

        var standings = await conn.QueryAsync<dynamic>(
            """
            SELECT
                ep.id AS participant_id,
                ep.name,
                ep.placement AS position,
                COUNT(bm.id) FILTER (WHERE bm.winner_id = ep.id) AS wins,
                COUNT(bm.id) FILTER (WHERE bm.status = 'completed' AND bm.winner_id IS NOT NULL AND bm.winner_id != ep.id AND (bm.team1_id = ep.id OR bm.team2_id = ep.id)) AS losses
            FROM tournament_external_participants ep
            LEFT JOIN brkt_matches bm ON (bm.team1_id = ep.id OR bm.team2_id = ep.id)
                AND bm.status = 'completed'
            WHERE ep.tournament_id = @id AND ep.organization_id = @orgId
            GROUP BY ep.id, ep.name, ep.placement
            ORDER BY ep.placement NULLS LAST, wins DESC
            """,
            new { id, orgId = apiCtx.OrgId });

        return Results.Ok(new { standings });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Match Handlers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListMatches(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.MatchesRead)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var matches = await conn.QueryAsync<dynamic>(
            """
            SELECT bm.id, bm.round_index AS round, bm.status, bm.team1_id AS participant1_id,
                   bm.team2_id AS participant2_id, bm.scheduled_time AS scheduled_at, bm.winner_id
            FROM brkt_matches bm
            JOIN brkt_versions bv ON bv.id = bm.version_id
            JOIN tournament_stages s ON s.id = bv.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE t.id = @id AND t.organization_id = @orgId
            ORDER BY bm.round_index, bm.match_number
            """,
            new { id, orgId = apiCtx.OrgId });

        return Results.Ok(new { matches });
    }

    private static async Task<IResult> GetMatch(
        Guid matchId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.MatchesRead)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT bm.id, s.tournament_id, bm.round_index AS round, bm.status,
                   bm.team1_id AS participant1_id, bm.team2_id AS participant2_id,
                   bm.scheduled_time AS scheduled_at, bm.winner_id,
                   bm.team1_score AS score_participant1, bm.team2_score AS score_participant2
            FROM brkt_matches bm
            JOIN brkt_versions bv ON bv.id = bm.version_id
            JOIN tournament_stages s ON s.id = bv.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE bm.id = @matchId AND t.organization_id = @orgId
            """,
            new { matchId, orgId = apiCtx.OrgId });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    private static async Task<IResult> ReportResult(
        Guid matchId,
        [FromBody] V1MatchResultRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.MatchesWrite)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(string Status, Guid? WinnerId)>(
            """
            SELECT bm.status, bm.winner_id
            FROM brkt_matches bm
            JOIN brkt_versions bv ON bv.id = bm.version_id
            JOIN tournament_stages s ON s.id = bv.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE bm.id = @matchId AND t.organization_id = @orgId
            """,
            new { matchId, orgId = apiCtx.OrgId });

        if (existing == default) return Results.NotFound();
        if (existing.Status == "completed")
            return Results.Conflict(new { error = "Match result has already been reported." });

        await conn.ExecuteAsync(
            "UPDATE brkt_matches SET winner_id = @winnerId, team1_score = @score1, team2_score = @score2, status = 'completed' WHERE id = @matchId",
            new { winnerId = req.WinnerId, score1 = req.ScoreParticipant1, score2 = req.ScoreParticipant2, matchId });

        return Results.Ok(new { match_id = matchId, winner_id = req.WinnerId, status = "completed" });
    }

    private static async Task<IResult> ScheduleMatch(
        Guid matchId,
        [FromBody] V1ScheduleMatchRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.MatchesWrite)) return Results.Forbid();

        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE brkt_matches bm
            SET scheduled_time = @scheduledAt
            FROM brkt_versions bv
            JOIN tournament_stages s ON s.id = bv.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE bm.id = @matchId AND bm.version_id = bv.id AND t.organization_id = @orgId
            """,
            new { matchId, scheduledAt = req.ScheduledAt, orgId = apiCtx.OrgId });

        return affected == 0 ? Results.NotFound() : Results.Ok(new { match_id = matchId, scheduled_at = req.ScheduledAt });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Veto Handlers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetVeto(
        Guid matchId,
        HttpContext ctx,
        VetoDbService veto,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.VetoRead)) return Results.Forbid();

        if (!await VerifyMatchOrgAsync(db, matchId, apiCtx.OrgId)) return Results.NotFound();

        var state = await veto.GetAsync(matchId, ct);
        return state is null ? Results.NotFound() : Results.Ok(state);
    }

    private static async Task<IResult> VetoPick(
        Guid matchId,
        [FromBody] V1VetoPickRequest req,
        HttpContext ctx,
        VetoDbService veto,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var apiCtx = ctx.Items["ApiKeyContext"] as ApiKeyContext;
        if (apiCtx is null) return Results.Unauthorized();
        if (!ApiKeyScopes.HasScope(apiCtx.Scopes, ApiKeyScopes.VetoWrite)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.Map)) return Results.BadRequest(new { error = "map is required" });

        if (!await VerifyMatchOrgAsync(db, matchId, apiCtx.OrgId)) return Results.NotFound();

        try
        {
            var result = req.Action?.ToLowerInvariant() == "ban"
                ? await veto.BanMapAsync(matchId, req.Map, req.ParticipantId, ct)
                : await veto.PickMapAsync(matchId, req.Map, req.ParticipantId, ct);
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Veto action not permitted." }, statusCode: 403);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.StartsWith("CONFLICT")
                ? Results.Conflict(new { error = "Veto state changed. Please refresh." })
                : Results.BadRequest(new { error = "Veto action is not valid at this time." });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> VerifyTournamentOrgAsync(IDbConnectionFactory db, Guid tournamentId, Guid orgId)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @tournamentId AND organization_id = @orgId)",
            new { tournamentId, orgId });
    }

    private static async Task<bool> VerifyMatchOrgAsync(IDbConnectionFactory db, Guid matchId, Guid orgId)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                JOIN tournament_stages s ON s.id = bv.stage_id
                JOIN tournaments t ON t.id = s.tournament_id
                WHERE bm.id = @matchId AND t.organization_id = @orgId
            )
            """,
            new { matchId, orgId });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Request records
// ─────────────────────────────────────────────────────────────────────────────

public sealed record V1CreateTournamentRequest(
    string Name,
    string Game,
    string Format,
    int MaxParticipants,
    DateTimeOffset StartDate,
    DateTimeOffset? EndDate = null,
    string? Region = null);

public sealed record V1PatchTournamentRequest(
    string? Name = null,
    DateTimeOffset? StartDate = null,
    DateTimeOffset? EndDate = null,
    int? MaxParticipants = null,
    string? Region = null);

public sealed record V1AddParticipantRequest(
    string ExternalId,
    string? Name = null,
    System.Text.Json.JsonElement? Metadata = null,
    int? Seeding = null);

public sealed record V1BracketGenerateRequest(
    Guid StageId,
    int BestOf,
    string? Format = null,
    int? BracketSize = null);

public sealed record V1SeedItem(Guid ParticipantId, int Position);

public sealed record V1SeedBracketRequest(List<V1SeedItem> Seeds);

public sealed record V1MatchResultRequest(Guid WinnerId, int ScoreParticipant1, int ScoreParticipant2);

public sealed record V1ScheduleMatchRequest(DateTimeOffset ScheduledAt);

public sealed record V1VetoPickRequest(
    string? Action,
    string Map,
    Guid ParticipantId);
