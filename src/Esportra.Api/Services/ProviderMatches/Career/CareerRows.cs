namespace Esportra.Api.Services.ProviderMatches.Career;

/// <summary>One verified game from a player's point of view. The team is the one they played for at report time.</summary>
public sealed record PlayerMatchRow(
    Guid MatchId, DateTime? StartedAt, string ContextType, Guid ContextId, Guid? ScrimSessionId, Guid? TournamentMatchId, int? GameNumber,
    string? MapId, Guid? TeamId, string? TeamName, Guid? OpponentId, string? OpponentName, int? RoundsWon, int? RoundsLost,
    string? CharacterId, int Score, int RoundsPlayed, int Kills, int Deaths, int Assists, int DamageDealt,
    int Headshots, int Bodyshots, int Legshots, int FirstKills, int FirstDeaths,
    int Kills2K, int Kills3K, int Kills4K, int Kills5K, int Plants, int Defuses);

/// <summary>One verified game from a team's point of view; the player figures are sums over the team's Riot side.</summary>
public sealed record TeamMatchRow(
    Guid MatchId, DateTime? StartedAt, string ContextType, Guid ContextId, Guid? ScrimSessionId, Guid? TournamentMatchId, int? GameNumber,
    string? MapId, Guid? OpponentId, string? OpponentName, int? RoundsWon, int? RoundsLost, int RoundsPlayed,
    int Kills, int Deaths, int Assists, int DamageDealt, int FirstKills, int FirstDeaths);

public static class ContextTypes
{
    public static bool IsTournament(string contextType) => contextType == ProviderMatchContext.TournamentGame;
}
