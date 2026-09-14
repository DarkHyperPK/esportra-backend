using System.Data;
using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Tournaments;

public sealed class StandingsResolutionService(
    IDbConnectionFactory db,
    IEnumerable<IStandingsResolver> resolvers)
{
    private static readonly Dictionary<string, string[]> FormatColumns = new()
    {
        ["single_elimination"] = ["rank", "team", "wins", "losses", "score_diff", "round_diff"],
        ["double_elimination"] = ["rank", "team", "bracket_side", "wins", "losses", "score_diff", "round_diff"],
        ["round_robin"] = ["rank", "team", "played", "wins", "losses", "ties", "points", "score_diff", "round_diff"],
        ["swiss"] = ["rank", "team", "played", "wins", "losses", "points", "buchholz", "round_results", "score_diff", "round_diff"],
        ["battle_royale"] = ["rank", "team", "played", "points", "kills", "wins"],
    };

    public async Task<TournamentStandingsResponse?> ResolveAsync(
        Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var stage = await LoadFinalStageAsync(conn, tournamentId);
        if (stage is null) return null;

        var confirmed = await LoadConfirmedPlacementsAsync(conn, tournamentId);
        var activeCount = await LoadActiveMatchCountAsync(conn, stage.StageId);
        var liveOpponents = await LoadLiveOpponentsAsync(conn, tournamentId);
        var brLiveTeams = stage.Format == "battle_royale"
            ? await LoadBrLiveTeamsAsync(conn, stage.StageId)
            : null;

        var resolver = resolvers.FirstOrDefault(r =>
            r.Format.Equals(stage.Format, StringComparison.OrdinalIgnoreCase))
            ?? resolvers.FirstOrDefault(r => r.Format == "single_elimination")!;

        var ctx = new StandingsContext(tournamentId, stage.StageId, stage.Config, conn);
        var rows = await resolver.ResolveAsync(ctx, ct);

        bool isComplete = rows.Count > 0 && confirmed.Count == rows.Count;
        bool isProvisional = !isComplete && activeCount == 0;
        var enriched = rows.Select(r => EnrichRow(r, confirmed, isProvisional, liveOpponents, brLiveTeams)).ToList();

        return new TournamentStandingsResponse
        {
            Format = stage.Format,
            IsComplete = isComplete,
            ComputedAt = DateTimeOffset.UtcNow,
            Columns = FormatColumns.GetValueOrDefault(stage.Format, FormatColumns["single_elimination"]),
            Rows = enriched,
        };
    }

    internal static StandingsRow EnrichRow(
        StandingsRow row,
        Dictionary<Guid, (string Label, decimal Amount, bool IsTied)> confirmed,
        bool isProvisional,
        Dictionary<Guid, string> liveByTeam,
        HashSet<Guid>? brLiveTeams = null)
    {
        var hasConfirmed = confirmed.TryGetValue(row.TeamId, out var c);
        var isBrLive = brLiveTeams?.Contains(row.TeamId) ?? false;
        liveByTeam.TryGetValue(row.TeamId, out var liveOpponent);
        return new StandingsRow
        {
            Rank = row.Rank,
            RankStatus = hasConfirmed ? "confirmed" : isProvisional ? "provisional" : "active",
            IsTied = hasConfirmed ? c.IsTied : row.IsTied,
            TeamId = row.TeamId,
            TeamName = row.TeamName,
            BracketSide = row.BracketSide,
            Played = row.Played,
            Wins = row.Wins,
            Losses = row.Losses,
            Ties = row.Ties,
            ScoreDiff = row.ScoreDiff,
            Points = row.Points,
            Buchholz = row.Buchholz,
            RoundResults = row.RoundResults,
            Kills = row.Kills,
            PrizeAmount = hasConfirmed ? c.Amount : 0m,
            PlacementLabel = hasConfirmed ? c.Label : null,
            IsLive = isBrLive || liveOpponent is not null,
            LiveOpponent = isBrLive ? null : liveOpponent,
        };
    }

    private static async Task<StageInfo?> LoadFinalStageAsync(IDbConnection conn, Guid tournamentId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT ts.id AS stage_id, ts.format, ts.config
            FROM tournaments t
            JOIN tournament_stages ts ON ts.tournament_id = t.id
            WHERE t.id = @tournamentId
            ORDER BY ts.stage_order DESC
            LIMIT 1
            """,
            new { tournamentId });
        if (row is null) return null;
        return new StageInfo((Guid)row.stage_id, (string?)row.format ?? "single_elimination", row.config);
    }

    private static async Task<Dictionary<Guid, (string Label, decimal Amount, bool IsTied)>>
        LoadConfirmedPlacementsAsync(IDbConnection conn, Guid tournamentId)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT team_id, placement_label, prize_amount, is_tied
            FROM tournament_placements
            WHERE tournament_id = @tournamentId
            """,
            new { tournamentId });
        return rows.ToDictionary(
            r => (Guid)r.team_id,
            r => ((string?)r.placement_label ?? "", (decimal?)r.prize_amount ?? 0m, (bool?)r.is_tied ?? false));
    }

    private static async Task<int> LoadActiveMatchCountAsync(IDbConnection conn, Guid stageId)
    {
        return await conn.QuerySingleAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            WHERE v.stage_id = @stageId
              AND m.status IN ('scheduled', 'in_progress')
              AND m.team1_id IS NOT NULL
              AND m.team2_id IS NOT NULL
            """,
            new { stageId });
    }

    private static async Task<Dictionary<Guid, string>> LoadLiveOpponentsAsync(
        IDbConnection conn, Guid tournamentId)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT m.team1_id, m.team2_id,
                   COALESCE(t1.name, tp1.team_name, sp1.username) AS team1_name,
                   COALESCE(t2.name, tp2.team_name, sp2.username) AS team2_name
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournament_stages ts ON ts.id = v.stage_id
            LEFT JOIN teams t1 ON t1.id = m.team1_id
            LEFT JOIN tournament_participants tp1 ON tp1.id = m.team1_id
              AND (tp1.is_mock = TRUE OR tp1.participant_type = 'solo')
            LEFT JOIN profiles sp1 ON sp1.id = tp1.user_id
            LEFT JOIN teams t2 ON t2.id = m.team2_id
            LEFT JOIN tournament_participants tp2 ON tp2.id = m.team2_id
              AND (tp2.is_mock = TRUE OR tp2.participant_type = 'solo')
            LEFT JOIN profiles sp2 ON sp2.id = tp2.user_id
            WHERE ts.tournament_id = @tournamentId
              AND m.status = 'in_progress'
            """,
            new { tournamentId });

        var result = new Dictionary<Guid, string>();
        foreach (var r in rows)
        {
            if (r.team1_id is Guid t1 && r.team2_id is Guid t2)
            {
                result.TryAdd(t1, (string?)r.team2_name ?? "Unknown");
                result.TryAdd(t2, (string?)r.team1_name ?? "Unknown");
            }
        }
        return result;
    }

    private static async Task<HashSet<Guid>> LoadBrLiveTeamsAsync(
        IDbConnection conn, Guid stageId)
    {
        var rows = await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT bgt.team_id
            FROM br_lobbies bl
            JOIN br_lobby_groups blg ON blg.lobby_id = bl.id
            JOIN br_group_teams bgt ON bgt.group_id = blg.group_id
            WHERE bl.stage_id = @stageId
              AND bl.status = 'active'
            """,
            new { stageId });
        return new HashSet<Guid>(rows);
    }

    private sealed record StageInfo(Guid StageId, string Format, object? Config);
}
