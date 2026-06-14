using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrLeaderboardRepository
{
    public static async Task<IReadOnlyList<BrLeaderboardRow>> ListForGroupAsync(
        IDbConnection conn,
        Guid groupId,
        BrTiebreaker tiebreaker,
        IDbTransaction? tx = null)
    {
        var hasParticipantId = await BrSchemaRepository.ColumnExistsAsync(
            conn, "br_lobby_results", "participant_id", tx);

        var rows = await conn.QueryAsync<dynamic>(
            hasParticipantId ? BrLeaderboardSql.GroupParticipant : BrLeaderboardSql.GroupTeamOnly,
            new { groupId },
            tx);

        var mapped = rows.Select(MapRow).Where(row => row is not null).Select(row => row!).ToList();
        return SortRows(mapped, tiebreaker);
    }

    public static async Task<IReadOnlyList<BrLeaderboardRow>> ListForStageAsync(
        IDbConnection conn,
        Guid stageId,
        BrTiebreaker tiebreaker,
        IDbTransaction? tx = null)
    {
        var rows = await conn.QueryAsync<dynamic>(
            BrLeaderboardSql.StageGlobal,
            new { stageId },
            tx);

        var mapped = rows.Select(MapRow).Where(row => row is not null).Select(row => row!).ToList();
        return SortRows(mapped, tiebreaker);
    }

    public static IReadOnlyList<object> ToGroupApiPayload(IReadOnlyList<BrLeaderboardRow> rows) =>
        rows.Select(row => new
        {
            team_id = row.TeamId,
            team_name = row.TeamName,
            logo_url = row.LogoUrl,
            games_played = row.GamesPlayed,
            total_placement_points = row.TotalPlacementPoints,
            total_kill_points = row.TotalKillPoints,
            total_points = row.TotalPoints,
            total_kills = row.TotalKills,
            wins = row.Wins,
            best_placement = row.BestPlacement,
        }).Cast<object>().ToList();

    public static IReadOnlyList<object> ToStageApiPayload(IReadOnlyList<BrLeaderboardRow> rows) =>
        rows.Select(row => new
        {
            team_id = row.TeamId,
            team_name = row.TeamName,
            logo_url = row.LogoUrl,
            games_played = row.GamesPlayed,
            total_placement_points = row.TotalPlacementPoints,
            total_kill_points = row.TotalKillPoints,
            total_points = row.TotalPoints,
            total_kills = row.TotalKills,
            wins = row.Wins,
            best_placement = row.BestPlacement,
            avg_placement = row.AvgPlacement,
        }).Cast<object>().ToList();

    private static List<BrLeaderboardRow> SortRows(IReadOnlyList<BrLeaderboardRow> rows, BrTiebreaker tiebreaker)
    {
        return rows
            .OrderBy(row => row, Comparer<BrLeaderboardRow>.Create((a, b) =>
            {
                var aggregateA = new BrLeaderboardAggregate(
                    a.TotalPoints, a.Wins, a.TotalKills, a.AvgPlacement ?? double.PositiveInfinity);
                var aggregateB = new BrLeaderboardAggregate(
                    b.TotalPoints, b.Wins, b.TotalKills, b.AvgPlacement ?? double.PositiveInfinity);
                return BrLeaderboardRanking.Compare(aggregateA, aggregateB, tiebreaker);
            }))
            .ToList();
    }

    private static BrLeaderboardRow? MapRow(dynamic row)
    {
        if (row.team_id is null) return null;

        return new BrLeaderboardRow(
            row.team_id.ToString(),
            row.team_name?.ToString() ?? "Unknown",
            row.logo_url?.ToString(),
            Convert.ToInt32(row.games_played ?? 0),
            Convert.ToInt64(row.total_placement_points ?? 0),
            Convert.ToInt64(row.total_kill_points ?? 0),
            Convert.ToInt64(row.total_points ?? 0),
            Convert.ToInt64(row.total_kills ?? 0),
            Convert.ToInt64(row.wins ?? 0),
            Convert.ToInt32(row.best_placement ?? 0),
            row.avg_placement is null ? null : Convert.ToDouble(row.avg_placement));
    }
}
