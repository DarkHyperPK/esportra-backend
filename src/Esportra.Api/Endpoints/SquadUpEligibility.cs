using System.Data;
using System.Text.Json.Serialization;
using Dapper;

namespace Esportra.Api.Endpoints;

/// <summary>What a player wants from Squad Up right now. Shared by Duo sessions and lobbies.</summary>
public sealed record SquadUpPreferences(
    [property: JsonPropertyName("game")] string? Game = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("wanted_countries")] string[]? WantedCountries = null,
    [property: JsonPropertyName("min_tier")] int? MinTier = null,
    [property: JsonPropertyName("max_tier")] int? MaxTier = null);

/// <summary>The player's facts that matching relies on, all read from the server, never from the client.</summary>
internal sealed record SquadUpPlayer(bool Linked, int? Tier, string? Region, string? Country);

/// <summary>Validated preferences, normalised: game and mode known, countries upper-case and non-empty.</summary>
internal sealed record SquadUpFilters(string Game, string Mode, string[] WantedCountries, int? MinTier, int? MaxTier);

internal static class SquadUpEligibility
{
    /// <summary>Games Squad Up supports. More titles are added here when they launch.</summary>
    public static readonly IReadOnlySet<string> SupportedGames = new HashSet<string> { "valorant" };

    public static readonly IReadOnlySet<string> Modes = new HashSet<string> { "competitive", "unrated" };

    public const int MaxWantedCountries = 60;
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
    public static (SquadUpFilters? Filters, string? Error) Normalise(SquadUpPreferences? prefs, string? ownCountry)
    {
        var game = (prefs?.Game ?? "valorant").Trim().ToLowerInvariant();
        if (!SupportedGames.Contains(game)) return (null, "UNSUPPORTED_GAME");
        var mode = (prefs?.Mode ?? "competitive").Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) return (null, "INVALID_MODE");
        var countries = (prefs?.WantedCountries ?? [])
            .Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToArray();
        if (countries.Length > MaxWantedCountries || countries.Any(c => c.Length != 2 || !c.All(char.IsAsciiLetterUpper)))
            return (null, "INVALID_COUNTRIES");
        if (countries.Length == 0 && ownCountry is not null) countries = [ownCountry];
        if (prefs?.MinTier is { } min && (min < MinValorantTier || min > MaxValorantTier)) return (null, "INVALID_RANK_RANGE");
        if (prefs?.MaxTier is { } max && (max < MinValorantTier || max > MaxValorantTier)) return (null, "INVALID_RANK_RANGE");
        if (prefs?.MinTier > prefs?.MaxTier) return (null, "INVALID_RANK_RANGE");
        // Rank range only means something in Competitive.
        var competitive = mode == "competitive";
        return (new SquadUpFilters(game, mode, countries, competitive ? prefs?.MinTier : null, competitive ? prefs?.MaxTier : null), null);
    }

    /// <returns>An error result when the player may not use Squad Up with these filters, else null.</returns>
    public static async Task<IResult?> CheckAsync(IDbConnection conn, Guid userId, SquadUpPlayer player, SquadUpFilters filters)
    {
        if (!player.Linked) return Results.UnprocessableEntity(new { error = "RIOT_NOT_LINKED" });
        if (filters.Mode == "competitive" && player.Tier is null) return Results.UnprocessableEntity(new { error = "NOT_RANKED" });
        if (string.IsNullOrWhiteSpace(player.Region)) return Results.UnprocessableEntity(new { error = "REGION_UNKNOWN" });
        if (string.IsNullOrWhiteSpace(player.Country)) return Results.UnprocessableEntity(new { error = "COUNTRY_REQUIRED" });
        var restrictions = (await conn.QueryAsync<DateTime?>(
            "SELECT until FROM public.squad_up_restrictions WHERE user_id = @userId AND lifted_at IS NULL AND (until IS NULL OR until > now()) ORDER BY until DESC NULLS FIRST",
            new { userId })).ToList();
        return restrictions.Count > 0
            ? Results.Json(new { error = "SQUAD_UP_RESTRICTED", until = restrictions[0] }, statusCode: StatusCodes.Status403Forbidden)
            : null;
    }
}
