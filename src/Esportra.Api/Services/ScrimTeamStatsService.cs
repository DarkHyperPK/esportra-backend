using System.Data;
using Dapper;
using Esportra.Contracts.Competitive;

namespace Esportra.Api.Services;

/// <summary>
/// Loads scrim records for many teams in one round trip via public.team_scrim_stats.
/// Teams that keep their scrim history private are absent from the result — callers
/// must treat a missing entry as "record hidden", never as zero.
/// </summary>
public static class ScrimTeamStatsService
{
    /// <summary>Records for one game only (a team's VALORANT history never shows on another game's listing). Null game = every game.</summary>
    public static async Task<IReadOnlyDictionary<Guid, ScrimTeamRecord>> LoadAsync(
        IDbConnection conn, IEnumerable<Guid> teamIds, string? gameSlug = null)
    {
        var ids = teamIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, ScrimTeamRecord>();

        var rows = await conn.QueryAsync<StatsRow>(
            """
            SELECT team_id, wins, losses, draws, completed, no_shows,
                   show_rate, last_played_at, last5, streak
            FROM public.team_scrim_stats(@ids, @gameSlug)
            """,
            new { ids, gameSlug = string.IsNullOrWhiteSpace(gameSlug) ? null : gameSlug.Trim() });

        return rows.ToDictionary(r => r.TeamId, r => new ScrimTeamRecord
        {
            Wins = r.Wins,
            Losses = r.Losses,
            Draws = r.Draws,
            Completed = r.Completed,
            NoShows = r.NoShows,
            ShowRate = r.ShowRate,
            LastPlayedAt = r.LastPlayedAt,
            Last5 = r.Last5 ?? [],
            Streak = r.Streak,
        });
    }

    /// <summary>Records keyed by team and game, for a list that mixes games: one read per game present.</summary>
    public static async Task<IReadOnlyDictionary<(Guid Team, string Game), ScrimTeamRecord>> LoadByGameAsync(
        IDbConnection conn, IEnumerable<(Guid Team, string Game)> pairs)
    {
        var result = new Dictionary<(Guid, string), ScrimTeamRecord>();
        foreach (var group in pairs.GroupBy(p => p.Game.ToLowerInvariant()))
        {
            var records = await LoadAsync(conn, group.Select(p => p.Team), group.Key);
            foreach (var (team, record) in records) result[(team, group.Key)] = record;
        }
        return result;
    }

    public static ScrimTeamRecord? Find(IReadOnlyDictionary<Guid, ScrimTeamRecord> map, Guid teamId) =>
        map.TryGetValue(teamId, out var record) ? record : null;

    private sealed class StatsRow
    {
        public Guid TeamId { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public int Draws { get; init; }
        public int Completed { get; init; }
        public int NoShows { get; init; }
        public decimal? ShowRate { get; init; }
        public DateTime? LastPlayedAt { get; init; }
        public string[]? Last5 { get; init; }
        public int Streak { get; init; }
    }
}
