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
    public static async Task<IReadOnlyDictionary<Guid, ScrimTeamRecord>> LoadAsync(
        IDbConnection conn, IEnumerable<Guid> teamIds)
    {
        var ids = teamIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, ScrimTeamRecord>();

        var rows = await conn.QueryAsync<StatsRow>(
            """
            SELECT team_id, wins, losses, draws, completed, no_shows,
                   show_rate, last_played_at, last5, streak
            FROM public.team_scrim_stats(@ids)
            """,
            new { ids });

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
