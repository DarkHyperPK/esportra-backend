using System.Data;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Squad Up Lobby mode: fill a whole team in real time.
/// POST /api/competitive/lfg/swipe/lobbies                                   — open a lobby (one per host)
/// GET  /api/competitive/lfg/swipe/lobbies?mode=&amp;countries=&amp;min_tier=&amp;max_tier= — compatible open lobbies
/// GET  /api/competitive/lfg/swipe/lobbies/mine                              — the lobby I host and the ones I asked to join
/// POST /api/competitive/lfg/swipe/lobbies/{id}/close                        — host closes the lobby
/// Membership (request / accept / decline / leave / kick) lives in <see cref="SquadUpLobbyMembership"/>.
/// </summary>
public static partial class SquadUpLobbyEndpoints
{
    private const string Base = "/api/competitive/lfg/swipe/lobbies";

    public static void MapSquadUpLobbyEndpoints(this WebApplication app)
    {
        app.MapPost(Base, CreateAsync).RequireAuthorization("Authenticated");
        app.MapGet(Base, BoardAsync).RequireAuthorization("Authenticated");
        app.MapGet($"{Base}/mine", MineAsync).RequireAuthorization("Authenticated");
        app.MapPost($"{Base}/{{id:guid}}/close", CloseAsync).RequireAuthorization("Authenticated");
        SquadUpLobbyMembership.Map(app, Base);
    }

    [GeneratedRegex("^[a-z_]{1,32}$")]
    private static partial Regex RoleKey();

    private static async Task<IResult> CreateAsync([FromBody] CreateLobbyRequest? req, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        if (req is null || req.OpenSlots is < 1 or > 4) return Results.BadRequest(new { error = "INVALID_SLOTS" });
        var title = (req.Title ?? "").Trim();
        if (title.Length > 60) return Results.BadRequest(new { error = "TITLE_TOO_LONG" });
        var roles = (req.WantedRoles ?? []).Select(r => r.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (roles.Length > 5 || roles.Any(r => !RoleKey().IsMatch(r))) return Results.BadRequest(new { error = "INVALID_ROLES" });

        using var conn = db.CreateConnection();
        var player = await SquadUpEligibility.LoadAsync(conn, me.UserIdGuid);
        var (filters, filterError) = SquadUpEligibility.Normalise(req.Preferences, player.Country);
        if (filters is null) return Results.BadRequest(new { error = filterError });
        var refused = await SquadUpEligibility.CheckAsync(conn, me.UserIdGuid, player, filters);
        if (refused is not null) return refused;

        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = $"squad-up-lobby-host:{me.UserIdGuid}" }, tx);
        await conn.ExecuteAsync(SquadUpLobbySql.CloseStale, new { hostId = me.UserIdGuid }, tx);
        if (await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM public.squad_up_lobbies WHERE host_id = @hostId AND status IN ('open', 'full'))",
                new { hostId = me.UserIdGuid }, tx))
            return Results.Conflict(new { error = "LOBBY_EXISTS" });
        var id = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.squad_up_lobbies (host_id, game_slug, mode, region, country_code, wanted_countries, competitive_tier,
                                                 min_tier, max_tier, title, open_slots, wanted_roles, mic_required)
            VALUES (@hostId, @game, @mode, @region, @country, @countries, @tier, @minTier, @maxTier, @title, @slots, @roles, @mic)
            RETURNING id
            """, new
            {
                hostId = me.UserIdGuid,
                game = filters.Game,
                mode = filters.Mode,
                region = player.Region,
                country = player.Country,
                countries = filters.WantedCountries,
                tier = player.Tier,
                minTier = filters.MinTier,
                maxTier = filters.MaxTier,
                title,
                slots = req.OpenSlots,
                roles,
                mic = req.MicRequired,
            }, tx);
        await SquadUpStats.BumpAsync(conn, player.Region!, SquadUpCounter.LobbiesOpened, tx);
        tx.Commit();
        return Results.Created($"{Base}/{id}", await LoadAsync(conn, id, me.UserIdGuid));
    }

    internal static Task<SquadUpLobbyDto?> LoadAsync(IDbConnection conn, Guid lobbyId, Guid myId, IDbTransaction? tx = null) =>
        conn.QuerySingleOrDefaultAsync<SquadUpLobbyDto>($"{SquadUpLobbySql.Select} WHERE l.id = @lobbyId", new { lobbyId, myId }, tx);

    private static async Task<IResult> BoardAsync(
        [FromQuery] string? mode, [FromQuery] string? countries, [FromQuery(Name = "min_tier")] int? minTier,
        [FromQuery(Name = "max_tier")] int? maxTier, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var player = await SquadUpEligibility.LoadAsync(conn, me.UserIdGuid);
        var prefs = new SquadUpPreferences("valorant", mode, countries?.Split(',', StringSplitOptions.RemoveEmptyEntries), minTier, maxTier);
        var (filters, filterError) = SquadUpEligibility.Normalise(prefs, player.Country);
        if (filters is null) return Results.BadRequest(new { error = filterError });
        var refused = await SquadUpEligibility.CheckAsync(conn, me.UserIdGuid, player, filters);
        if (refused is not null) return refused;
        var lobbies = await conn.QueryAsync<SquadUpLobbyDto>(SquadUpLobbySql.Board, new
        {
            myId = me.UserIdGuid,
            game = filters.Game,
            mode = filters.Mode,
            region = player.Region,
            country = player.Country,
            countries = filters.WantedCountries,
            tier = player.Tier,
            minTier = filters.MinTier,
            maxTier = filters.MaxTier,
        });
        return Results.Ok(lobbies);
    }

    private static async Task<IResult> MineAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(SquadUpLobbySql.CloseStale, new { hostId = me.UserIdGuid });
        var hosting = await conn.QuerySingleOrDefaultAsync<SquadUpLobbyDto>(
            $"{SquadUpLobbySql.Select} WHERE l.host_id = @myId AND l.status IN ('open', 'full') ORDER BY l.created_at DESC LIMIT 1",
            new { myId = me.UserIdGuid });
        var members = hosting is null
            ? []
            : (await conn.QueryAsync<SquadUpLobbyMemberDto>(SquadUpLobbySql.Members, new { lobbyId = hosting.Id })).ToList();
        var joined = await conn.QueryAsync<SquadUpLobbyDto>(
            $"""
            {SquadUpLobbySql.Select}
            WHERE l.host_id <> @myId AND l.status IN ('open', 'full')
              AND EXISTS (SELECT 1 FROM public.squad_up_lobby_members jm
                          WHERE jm.lobby_id = l.id AND jm.user_id = @myId AND jm.status IN ('requested', 'accepted'))
            ORDER BY l.created_at DESC LIMIT 10
            """, new { myId = me.UserIdGuid });
        return Results.Ok(new { hosting, members, joined });
    }

    private static async Task<IResult> CloseAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var closed = await conn.ExecuteAsync(
            "UPDATE public.squad_up_lobbies SET status = 'closed', closed_at = now() WHERE id = @id AND host_id = @hostId AND status IN ('open', 'full')",
            new { id, hostId = me.UserIdGuid });
        if (closed == 0) return Results.NotFound();
        await SquadUpLobbyMembership.PushUpdatedAsync(conn, hub, id);
        return Results.NoContent();
    }
}

public sealed record CreateLobbyRequest(
    [property: JsonPropertyName("open_slots")] int OpenSlots,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("wanted_roles")] string[]? WantedRoles = null,
    [property: JsonPropertyName("mic_required")] bool MicRequired = false,
    [property: JsonPropertyName("game")] string? Game = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("wanted_countries")] string[]? WantedCountries = null,
    [property: JsonPropertyName("min_tier")] int? MinTier = null,
    [property: JsonPropertyName("max_tier")] int? MaxTier = null)
{
    [JsonIgnore]
    public SquadUpPreferences Preferences => new(Game, Mode, WantedCountries, MinTier, MaxTier);
}

public sealed record SquadUpLobbyDto
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public string? HostUsername { get; init; }
    public string? HostAvatarUrl { get; init; }
    public string? HostAvatarSeed { get; init; }
    public string? HostAvatarStyle { get; init; }
    public int? HostTier { get; init; }
    public string? HostTierName { get; init; }
    public string Title { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Region { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public string[] WantedCountries { get; init; } = [];
    public int? MinTier { get; init; }
    public int? MaxTier { get; init; }
    public int OpenSlots { get; init; }
    public int AcceptedCount { get; init; }
    public string[] WantedRoles { get; init; } = [];
    public bool MicRequired { get; init; }
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string? MyStatus { get; init; }
    public Guid? ConversationId { get; init; }
    public string MembersJson { get; init; } = "[]";
}

public sealed record SquadUpLobbyMemberDto
{
    public Guid UserId { get; init; }
    public string? Username { get; init; }
    public string? AvatarUrl { get; init; }
    public string? AvatarSeed { get; init; }
    public string? AvatarStyle { get; init; }
    public string? RoleKey { get; init; }
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public int? CompetitiveTier { get; init; }
    public string? CompetitiveTierName { get; init; }
    public string? RiotGameName { get; init; }
    public string? RiotTagLine { get; init; }
}
