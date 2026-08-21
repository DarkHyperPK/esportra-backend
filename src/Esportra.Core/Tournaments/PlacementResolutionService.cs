using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Br;
using Esportra.Core.Bracket;

namespace Esportra.Core.Tournaments;

public sealed class PlacementResolutionService(
    IDbConnectionFactory db,
    StandingsService standings,
    PrizeDistributionService prizeService)
{
    public async Task<List<ResolvedPlacement>> ResolveAsync(
        Guid tournamentId,
        bool force = false,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        if (!force)
        {
            int existing = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*)::int FROM tournament_placements WHERE tournament_id = @tournamentId",
                new { tournamentId });
            if (existing > 0)
            {
                var cached = await conn.QueryAsync<dynamic>(
                    """
                    SELECT tp.team_id, t.name AS team_name, tp.placement, tp.placement_label,
                           tp.prize_amount, tp.prize_rewards, tp.is_tied
                    FROM tournament_placements tp
                    JOIN teams t ON t.id = tp.team_id
                    WHERE tp.tournament_id = @tournamentId
                    ORDER BY tp.placement
                    """,
                    new { tournamentId });
                return cached.Select(MapCachedRow).ToList();
            }
        }

        var computation = await ComputeInternalAsync(conn, tournamentId, ct);
        if (computation is null || computation.Placements.Count == 0) return [];

        await PersistAsync(conn, tournamentId, computation.Placements,
            computation.Currency, computation.PayoutMethod, computation.ManualPayoutNotes, force, ct);
        return computation.Placements;
    }

    public async Task<List<ResolvedPlacement>> ComputeCurrentAsync(
        Guid tournamentId,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var result = await ComputeInternalAsync(conn, tournamentId, ct);
        return result?.Placements ?? [];
    }

    private async Task<ComputationResult?> ComputeInternalAsync(
        IDbConnection conn, Guid tournamentId, CancellationToken ct)
    {
        var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, prize_pool, prize_distribution, currency, payout_method, manual_payout_notes FROM tournaments WHERE id = @tournamentId",
            new { tournamentId });

        if (tournament is null) return null;

        var finalStage = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT id, format, config
            FROM tournament_stages
            WHERE tournament_id = @tournamentId
            ORDER BY stage_order DESC
            LIMIT 1
            """,
            new { tournamentId });

        if (finalStage is null) return null;

        string format = (string?)finalStage.format ?? "single_elimination";
        Guid stageId = (Guid)finalStage.id;
        object? stageConfig = finalStage.config;

        List<TeamPlacement> orderedTeams = format.ToLowerInvariant() switch
        {
            "single_elimination" => await ResolveSingleEliminationAsync(conn, stageId, ct),
            "double_elimination" => await ResolveDoubleEliminationAsync(conn, stageId, ct),
            "round_robin" => await ResolveStandingsAsync(conn, stageId, ct),
            "swiss" => await ResolveStandingsAsync(conn, stageId, ct),
            "battle_royale" => await ResolveBattleRoyaleAsync(conn, stageId, stageConfig, ct),
            _ => await ResolveSingleEliminationAsync(conn, stageId, ct),
        };

        if (orderedTeams.Count == 0) return null;

        decimal prizePool = (decimal?)tournament.prize_pool ?? 0m;
        string currency = (string?)tournament.currency ?? "USD";
        string payoutMethod = (string?)tournament.payout_method ?? "manual";
        string? manualPayoutNotes = (string?)tournament.manual_payout_notes;
        PrizeDistributionConfig? config = ParseDistributionConfig((string?)tournament.prize_distribution);

        List<ResolvedPlacement> resolved = config is null || config.Placements.Count == 0
            ? orderedTeams
                .Select(t => new ResolvedPlacement(
                    t.TeamId, t.TeamName, t.Placement,
                    OrdinalLabel(t.Placement), 0m, [], t.Placement > 1))
                .ToList()
            : prizeService.CalculateAmounts(config, prizePool, orderedTeams
                .Select(t => (t.TeamId, t.TeamName, t.Placement))
                .ToList());

        return new ComputationResult(resolved, currency, payoutMethod, manualPayoutNotes);
    }

    private async Task<List<TeamPlacement>> ResolveSingleEliminationAsync(
        IDbConnection conn, Guid stageId, CancellationToken ct)
    {
        // Query all completed matches ordered by round descending.
        // Grand final (round_index max, bracket_type = 'final') gives 1st and 2nd.
        // Each earlier round's losers share the next placement band.
        var matches = (await conn.QueryAsync<dynamic>(
            """
            SELECT m.round_index, m.bracket_type, m.winner_id, m.loser_id,
                   wt.name AS winner_name, lt.name AS loser_name
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            LEFT JOIN teams wt ON wt.id = m.winner_id
            LEFT JOIN teams lt ON lt.id = m.loser_id
            WHERE v.stage_id = @stageId
              AND m.status = 'completed'
              AND m.bracket_type IN ('final', 'winners', 'group')
            ORDER BY m.round_index DESC
            """,
            new { stageId })).AsList();

        var result = new List<TeamPlacement>();
        int nextPlacement = 1;
        var seen = new HashSet<Guid>();

        // Group by round (highest = latest)
        var byRound = matches
            .GroupBy(m => (int)m.round_index)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var roundGroup in byRound)
        {
            var roundMatches = roundGroup.ToList();

            // Winners of the highest round = 1st place (only once)
            if (nextPlacement == 1)
            {
                foreach (var m in roundMatches)
                {
                    if (m.winner_id is Guid wId && !seen.Contains(wId))
                    {
                        seen.Add(wId);
                        result.Add(new(wId, (string?)m.winner_name ?? "Unknown", nextPlacement));
                        nextPlacement++;
                    }
                }
            }

            // Losers of this round share the next band
            var losers = new List<(Guid Id, string Name)>();
            foreach (var m in roundMatches)
            {
                if (m.loser_id is null) continue;
                var lid = (Guid)m.loser_id;
                if (!seen.Contains(lid))
                    losers.Add((lid, (string?)m.loser_name ?? "Unknown"));
            }

            foreach (var loser in losers)
            {
                seen.Add(loser.Id);
                result.Add(new(loser.Id, loser.Name, nextPlacement));
            }

            if (losers.Count > 0)
                nextPlacement += losers.Count;
        }

        return result;
    }

    private async Task<List<TeamPlacement>> ResolveDoubleEliminationAsync(
        IDbConnection conn, Guid stageId, CancellationToken ct)
    {
        var matches = (await conn.QueryAsync<dynamic>(
            """
            SELECT m.round_index, m.bracket_type, m.winner_id, m.loser_id,
                   wt.name AS winner_name, lt.name AS loser_name
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            LEFT JOIN teams wt ON wt.id = m.winner_id
            LEFT JOIN teams lt ON lt.id = m.loser_id
            WHERE v.stage_id = @stageId
              AND m.status = 'completed'
            ORDER BY
              CASE m.bracket_type WHEN 'final' THEN 0 WHEN 'losers' THEN 1 ELSE 2 END,
              m.round_index DESC
            """,
            new { stageId })).AsList();

        var result = new List<TeamPlacement>();
        var seen = new HashSet<Guid>();
        int nextPlacement = 1;

        // Grand final: winner = 1st, loser = 2nd
        var grandFinal = matches.FirstOrDefault(m =>
            string.Equals((string?)m.bracket_type, "final", StringComparison.OrdinalIgnoreCase));

        if (grandFinal is not null)
        {
            if (grandFinal.winner_id is Guid gfWin && seen.Add(gfWin))
                result.Add(new(gfWin, (string?)grandFinal.winner_name ?? "Unknown", nextPlacement++));
            if (grandFinal.loser_id is Guid gfLose && seen.Add(gfLose))
                result.Add(new(gfLose, (string?)grandFinal.loser_name ?? "Unknown", nextPlacement++));
        }

        // Losers bracket matches, ordered by latest round first — each loser gets next placement
        var loserMatches = matches
            .Where(m => string.Equals((string?)m.bracket_type, "losers", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => (int)m.round_index)
            .ToList();

        // Group losers bracket by round — each round's losers share a placement band
        foreach (var roundGroup in loserMatches.GroupBy(m => (int)m.round_index).OrderByDescending(g => g.Key))
        {
            var bandLosers = new List<(Guid Id, string Name)>();
            foreach (var m in roundGroup)
            {
                if (m.loser_id is null) continue;
                var lId = (Guid)m.loser_id;
                if (seen.Add(lId))
                    bandLosers.Add((lId, (string?)m.loser_name ?? "Unknown"));
            }
            foreach (var loser in bandLosers)
                result.Add(new(loser.Id, loser.Name, nextPlacement));
            if (bandLosers.Count > 0)
                nextPlacement += bandLosers.Count;
        }

        // Winners bracket losers (eliminated before reaching the grand final)
        var winnerMatches = matches
            .Where(m => string.Equals((string?)m.bracket_type, "winners", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => (int)m.round_index)
            .ToList();

        foreach (var roundGroup in winnerMatches.GroupBy(m => (int)m.round_index).OrderByDescending(g => g.Key))
        {
            var losers = new List<(Guid Id, string Name)>();
            foreach (var m in roundGroup)
            {
                if (m.loser_id is null) continue;
                var lid = (Guid)m.loser_id;
                if (seen.Add(lid))
                    losers.Add((lid, (string?)m.loser_name ?? "Unknown"));
            }
            foreach (var loser in losers)
                result.Add(new(loser.Id, loser.Name, nextPlacement));
            if (losers.Count > 0)
                nextPlacement += losers.Count;
        }

        return result;
    }

    private async Task<List<TeamPlacement>> ResolveStandingsAsync(
        IDbConnection conn, Guid stageId, CancellationToken ct)
    {
        var standingsList = await standings.CalculateStandingsAsync(stageId, ct: ct);
        return standingsList
            .Select(s => new TeamPlacement(s.TeamId, s.TeamName, s.Rank))
            .ToList();
    }

    private async Task<List<TeamPlacement>> ResolveBattleRoyaleAsync(
        IDbConnection conn, Guid stageId, object? stageConfig, CancellationToken ct)
    {
        var tiebreaker = BrConfigService.ResolveTiebreaker(stageConfig);
        var rows = await BrLeaderboardRepository.ListForStageAsync(conn, stageId, tiebreaker);
        return rows
            .Select((r, i) => new TeamPlacement(
                Guid.TryParse(r.TeamId, out var tid) ? tid : Guid.Empty,
                r.TeamName,
                i + 1))
            .Where(t => t.TeamId != Guid.Empty)
            .ToList();
    }

    private async Task PersistAsync(
        IDbConnection conn,
        Guid tournamentId,
        List<ResolvedPlacement> placements,
        string currency,
        string payoutMethod,
        string? manualPayoutNotes,
        bool force,
        CancellationToken ct)
    {
        if (force)
        {
            await conn.ExecuteAsync(
                "DELETE FROM tournament_placements WHERE tournament_id = @tournamentId",
                new { tournamentId });
            await conn.ExecuteAsync(
                "DELETE FROM tournament_reward_distributions WHERE tournament_id = @tournamentId",
                new { tournamentId });
            await conn.ExecuteAsync(
                "DELETE FROM tournament_cash_payouts WHERE tournament_id = @tournamentId",
                new { tournamentId });
        }

        foreach (var p in placements)
        {
            var rewardsJson = JsonSerializer.Serialize(p.Rewards);
            await conn.ExecuteAsync(
                """
                INSERT INTO tournament_placements
                    (tournament_id, team_id, placement, placement_label, prize_amount, prize_rewards, is_tied)
                VALUES
                    (@tournamentId, @teamId, @placement, @label, @amount, @rewards::jsonb, @isTied)
                ON CONFLICT (tournament_id, team_id) DO UPDATE SET
                    placement       = EXCLUDED.placement,
                    placement_label = EXCLUDED.placement_label,
                    prize_amount    = EXCLUDED.prize_amount,
                    prize_rewards   = EXCLUDED.prize_rewards,
                    is_tied         = EXCLUDED.is_tied,
                    resolved_at     = NOW()
                """,
                new
                {
                    tournamentId,
                    teamId = p.TeamId,
                    placement = p.Placement,
                    label = p.PlacementLabel,
                    amount = p.PrizeAmount,
                    rewards = rewardsJson,
                    isTied = p.IsTied,
                });

            // Seed one reward-distribution tracking row per organizer-managed reward.
            // These let organizers mark each reward as distributed/claimed/cancelled.
            var orgRewards = p.Rewards
                .Select((r, i) => (Reward: r, Index: i))
                .Where(x => RewardType.IsOrganizerManaged(x.Reward.Type))
                .ToList();

            foreach (var (reward, rewardIndex) in orgRewards)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tournament_reward_distributions
                        (tournament_id, team_id, placement, reward_index, reward_title, reward_type, status)
                    VALUES
                        (@tournamentId, @teamId, @placement, @rewardIndex, @rewardTitle, @rewardType, 'pending')
                    ON CONFLICT (tournament_id, team_id, reward_index) DO NOTHING
                    """,
                    new
                    {
                        tournamentId,
                        teamId = p.TeamId,
                        placement = p.Placement,
                        rewardIndex,
                        rewardTitle = reward.Title,
                        rewardType = reward.Type,
                    });
            }

            // Seed one cash payout row for teams with a non-zero prize amount.
            // Gateway fields remain NULL until a payment gateway is integrated.
            if (p.PrizeAmount > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tournament_cash_payouts
                        (tournament_id, team_id, placement, amount, currency,
                         payment_method, manual_payment_notes, status)
                    VALUES
                        (@tournamentId, @teamId, @placement, @amount, @currency,
                         @paymentMethod, @manualPaymentNotes, 'requested')
                    ON CONFLICT (tournament_id, team_id) DO NOTHING
                    """,
                    new
                    {
                        tournamentId,
                        teamId = p.TeamId,
                        placement = p.Placement,
                        amount = p.PrizeAmount,
                        currency,
                        paymentMethod = payoutMethod,
                        manualPaymentNotes = manualPayoutNotes,
                    });
            }
        }
    }

    private static PrizeDistributionConfig? ParseDistributionConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]" || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<PrizeDistributionConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            return null;
        }
    }

    private static ResolvedPlacement MapCachedRow(dynamic row)
    {
        var rewards = new List<PrizeReward>();
        try
        {
            if (row.prize_rewards is string json && !string.IsNullOrWhiteSpace(json))
                rewards = JsonSerializer.Deserialize<List<PrizeReward>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { }

        return new ResolvedPlacement(
            (Guid)row.team_id,
            (string?)row.team_name ?? "Unknown",
            (int)row.placement,
            (string?)row.placement_label ?? "",
            (decimal?)row.prize_amount ?? 0m,
            rewards,
            (bool?)row.is_tied ?? false);
    }

    private static string OrdinalLabel(int position) => position switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{position}th",
    };

    private sealed record TeamPlacement(Guid TeamId, string TeamName, int Placement);

    private sealed record ComputationResult(
        List<ResolvedPlacement> Placements,
        string Currency,
        string PayoutMethod,
        string? ManualPayoutNotes);
}
