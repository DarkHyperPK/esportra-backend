namespace Esportra.Api.Services.ProviderMatches.Career;

/// <summary>One scrim map from a team's side, with the five agents it fielded and its attack/defense rounds (zero when unknown).</summary>
public sealed record TeamGameDetail(
    Guid MatchId, DateTime? StartedAt, int? GameNumber, string? MapId, int? RoundsWon, int? RoundsLost, int RoundsPlayed, string[] Agents,
    int AttackPlayed, int AttackWon, int DefensePlayed, int DefenseWon);

/// <summary>A roster member who played a map for the team.</summary>
public sealed record GamePlayer(Guid MatchId, Guid UserId, string? Username, string? MemberRole);
