using Esportra.Api.Services.ProviderMatches.Career;

namespace Esportra.Api.Services.ScrimData;

/// <summary>How much a scrim map can tell us. Everything counts for the result; only some tiers carry more.</summary>
public static class DataTier
{
    /// <summary>An auto-fetched Riot game whose association was verified: per-round detail, every player's stats.</summary>
    public const string Full = "full";

    /// <summary>A screenshot (OCR) map: what the scoreboard shows per player, with no round-by-round data.</summary>
    public const string Screenshot = "screenshot";

    /// <summary>A manually entered map, or a Riot game that is pending or unverified: the result and the map only.</summary>
    public const string ScoreOnly = "score_only";
}

/// <summary>One confirmed scrim map, from the team's point of view, as stored.</summary>
public sealed record ScrimMapRow(
    Guid GameId, Guid SessionId, int GameNumber, DateTime? PlayedAt, string Source, string? MapName, int Team1Score, int Team2Score,
    int? WinnerSlot, int TeamSlot, string? OpponentName, string? AssociationStatus, string? MatchDataStatus, string? OcrMatchData, bool? IsCustom = null);

/// <summary>Riot-derived facts for the team's side of a verified scrim map.</summary>
public sealed record RiotGameRow(
    Guid GameId, string? MapId, int RoundsPlayed, string[] Agents, int AttackPlayed, int AttackWon, int DefensePlayed, int DefenseWon);

public sealed record RiotPlayerRow(
    Guid GameId, Guid? UserId, string? Name, string? CharacterId, string? MemberRole, int Kills, int Deaths, int Assists, int Score,
    int RoundsPlayed, int DamageDealt, int Headshots, int Bodyshots, int Legshots, int FirstKills, int FirstDeaths, int Plants, int Defuses);

/// <summary>One player's line in one map. A figure is null when the source did not provide it.</summary>
public sealed record ScrimPlayerLine(
    Guid GameId, string Tier, Guid? UserId, string? Name, string? CharacterId, string? MemberRole,
    int? Kills, int? Deaths, int? Assists, double? Acs, int? RoundsPlayed, double? Adr, double? HsPct,
    int? FirstKills, int? FirstDeaths, int? Plants, int? Defuses);

public sealed record ScrimGame(
    ScrimMapRow Row, string Tier, string Result, string? Map, TeamGameDetail Detail, IReadOnlyList<ScrimPlayerLine> Lines);

/// <summary>Why a map is not in the full-detail tier, so a team can tell "still loading" from "does not match".</summary>
public sealed record Coverage(
    int Maps, int Full, int Screenshot, int ScoreOnly, int RiotPending, int RiotNotVerified, int AwaitingConfirmation, int Disputed, int QueueGames = 0);

public sealed record ScrimDataSet(IReadOnlyList<ScrimGame> Games, Coverage Coverage);
