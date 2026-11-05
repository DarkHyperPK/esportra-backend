using System.Data;
using System.Text.Json.Serialization;
using Dapper;

namespace Esportra.Api.Endpoints;

/// <summary>What a player wants from Squad Up right now. Shared by Duo sessions and lobbies.</summary>
public sealed record SquadUpPreferences(
    [property: JsonPropertyName("game")] string? Game = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("area")] string? Area = null,
    [property: JsonPropertyName("min_tier")] int? MinTier = null,
    [property: JsonPropertyName("max_tier")] int? MaxTier = null);

/// <summary>The player's facts that matching relies on, all read from the server, never from the client.</summary>
internal sealed record SquadUpPlayer(bool Linked, int? Tier, string? Region, string? Country);

/// <summary>Validated preferences, normalised: game, mode and area known.</summary>
internal sealed record SquadUpFilters(string Game, string Mode, string Area, int? MinTier, int? MaxTier);

internal static class SquadUpEligibility
{
    /// <summary>Games Squad Up supports. More titles are added here when they launch.</summary>
    public static readonly IReadOnlySet<string> SupportedGames = new HashSet<string> { "valorant" };

    public static readonly IReadOnlySet<string> Modes = new HashSet<string> { "competitive", "unrated" };

    /// <summary>Where a player lives. Players only meet players from the same area (the client groups countries the same way).</summary>
    public static readonly IReadOnlySet<string> Areas = new HashSet<string>
    {
        "south-asia", "middle-east", "europe", "central-asia", "east-asia", "southeast-asia", "oceania",
        "north-america", "latin-america", "africa",
    };

    public const int MaxNoteLength = 100;
    public const int MinValorantTier = 3;
    public const int MaxValorantTier = 27;

    public static Task<SquadUpPlayer> LoadAsync(IDbConnection conn, Guid userId, IDbTransaction? tx = null) =>
        conn.QuerySingleAsync<SquadUpPlayer>(
            """
            SELECT EXISTS (SELECT 1 FROM public.riot_accounts WHERE user_id = @userId) AS linked,
                   (SELECT pgs.competitive_tier FROM public.player_game_stats pgs
                    WHERE pgs.user_id = @userId AND pgs.game_slug = 'valorant'
                      AND EXISTS (SELECT 1 FROM public.riot_accounts l WHERE l.user_id = pgs.user_id AND l.puuid = pgs.provider_player_id)) AS tier,
                   (SELECT lower(region) FROM public.riot_accounts WHERE user_id = @userId) AS region,
                   (SELECT upper(country_code) FROM public.profiles WHERE id = @userId) AS country
            """, new { userId }, tx);

    /// <returns>Normalised filters, or an error code for a 400.</returns>
    public static (SquadUpFilters? Filters, string? Error) Normalise(SquadUpPreferences? prefs)
    {
        var game = (prefs?.Game ?? "valorant").Trim().ToLowerInvariant();
        if (!SupportedGames.Contains(game)) return (null, "UNSUPPORTED_GAME");
        var mode = (prefs?.Mode ?? "competitive").Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) return (null, "INVALID_MODE");
        var area = (prefs?.Area ?? "").Trim().ToLowerInvariant();
        if (!Areas.Contains(area)) return (null, "INVALID_AREA");
        if (prefs?.MinTier is { } min && (min < MinValorantTier || min > MaxValorantTier)) return (null, "INVALID_RANK_RANGE");
        if (prefs?.MaxTier is { } max && (max < MinValorantTier || max > MaxValorantTier)) return (null, "INVALID_RANK_RANGE");
        if (prefs?.MinTier > prefs?.MaxTier) return (null, "INVALID_RANK_RANGE");
        // Rank range only means something in Competitive.
        var competitive = mode == "competitive";
        return (new SquadUpFilters(game, mode, area, competitive ? prefs?.MinTier : null, competitive ? prefs?.MaxTier : null), null);
    }

    /// <returns>An error result when the player may not use Squad Up with these filters, else null.</returns>
    public static async Task<IResult?> CheckAsync(IDbConnection conn, Guid userId, SquadUpPlayer player, SquadUpFilters filters)
    {
        if (!player.Linked) return Results.UnprocessableEntity(new { error = "RIOT_NOT_LINKED" });
        if (filters.Mode == "competitive" && player.Tier is null) return Results.UnprocessableEntity(new { error = "NOT_RANKED" });
        if (string.IsNullOrWhiteSpace(player.Region)) return Results.UnprocessableEntity(new { error = "REGION_UNKNOWN" });
        var restrictions = (await conn.QueryAsync<DateTime?>(
            "SELECT until FROM public.squad_up_restrictions WHERE user_id = @userId AND lifted_at IS NULL AND (until IS NULL OR until > now()) ORDER BY until DESC NULLS FIRST",
            new { userId })).ToList();
        return restrictions.Count > 0
            ? Results.Json(new { error = "SQUAD_UP_RESTRICTED", until = restrictions[0] }, statusCode: StatusCodes.Status403Forbidden)
            : null;
    }

    /// <summary>Trims a card note to one tidy line. Returns null for an empty note; error when it is too long.</summary>
    public static (string? Note, string? Error) NormaliseNote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        // Control characters (newlines, tabs) become spaces, then runs of spaces collapse to one.
        var clean = string.Join(' ', new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) return (null, null);
        return clean.Length > MaxNoteLength ? (null, "NOTE_TOO_LONG") : (clean, null);
    }
}
