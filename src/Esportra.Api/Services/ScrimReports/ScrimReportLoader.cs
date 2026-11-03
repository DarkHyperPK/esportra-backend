using System.Data;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>Loads a team's recent verified maps and builds each one's report, ready for the team and player aggregates.</summary>
public static class ScrimReportLoader
{
    public static async Task<(List<ReportMatch> Matches, bool Capped)> LoadAsync(IDbConnection conn, Guid teamId)
    {
        var heads = await ScrimReportQueries.HeadsAsync(conn, teamId);
        var capped = heads.Count > ScrimReportQueries.MapCap;
        if (capped) heads = heads.Take(ScrimReportQueries.MapCap).ToList();
        if (heads.Count == 0) return ([], false);

        var (matches, users) = await ScrimReportQueries.LoadAsync(conn, heads);
        var result = heads.Select(h =>
        {
            var match = matches[h.GameId];
            var (mine, theirs) = h.MySlot == 1 ? (h.Team1Score, h.Team2Score) : (h.Team2Score, h.Team1Score);
            var outcome = h.WinnerSlot is { } w ? (w == h.MySlot ? "win" : "loss") : mine > theirs ? "win" : mine < theirs ? "loss" : "draw";
            var map = ProviderMatchVerifier.DisplayMap(h.MapId) ?? ValorantMaps.Resolve(h.MapName);
            var game = new ReportGame(h.GameId, h.StartedAt, string.IsNullOrEmpty(map) ? null : map, h.OpponentId, h.OpponentName, outcome, mine, theirs);
            return new ReportMatch(game, match, ScrimInsightsBuilder.Build(match), users[h.GameId]);
        }).ToList();
        return (result, capped);
    }
}
