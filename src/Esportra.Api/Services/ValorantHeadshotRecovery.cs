using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>Repairs hit totals without rebuilding or reducing existing match/KD/WR totals.</summary>
internal static class ValorantHeadshotRecovery
{
    internal sealed record Totals
    {
        public int Matches { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public int Kills { get; init; }
        public int Deaths { get; init; }
        public int Assists { get; init; }
        public int Score { get; init; }
        public string? PopularAgents { get; init; }
    }

    internal sealed record Recovered(int Headshots, int Shots, decimal? Percentage, string? PopularAgents);
    private sealed record LinkedAct(string Puuid, string ActId);

    internal static async Task RepairSavedAsync(IDbConnection conn, Guid userId, CancellationToken ct)
    {
        var key = $"valorant-stats:{userId}";
        var locked = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", new { key }, cancellationToken: ct));
        if (!locked) return;
        try
        {
            var act = await conn.QuerySingleOrDefaultAsync<LinkedAct>(new CommandDefinition("""
                SELECT ra.puuid, p.last_act_id AS actid FROM public.riot_accounts ra
                JOIN public.player_game_stats p ON p.user_id = ra.user_id AND p.game_slug = 'valorant'
                    AND p.provider_player_id = ra.puuid
                WHERE ra.user_id = @userId AND p.last_act_id IS NOT NULL
                """, new { userId }, cancellationToken: ct));
            if (act is not null) await RepairAsync(conn, userId, act.Puuid, act.ActId);
        }
        finally { await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key }); }
    }

    internal static async Task RepairAsync(IDbConnection conn, Guid userId, string puuid, string actId)
    {
        // Exact archive/counter coverage below proves hit completeness independently
        // of the version used to build the player's other aggregate fields.
        var totals = await conn.QuerySingleOrDefaultAsync<Totals>("""
            SELECT matches_analyzed AS matches, win_count AS wins, loss_count AS losses,
                total_kills AS kills, total_deaths AS deaths, total_assists AS assists,
                total_score AS score, popular_agents::text AS popular_agents
            FROM public.player_game_stats
            WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
              AND last_act_id = @actId AND matches_analyzed > 0
              AND (headshot_data_complete IS NOT TRUE OR hs_pct IS NULL OR EXISTS (
                  SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(popular_agents) = 'array'
                      THEN popular_agents ELSE '[]'::jsonb END) agent
                  WHERE agent->>'headshot_data_complete' IS DISTINCT FROM 'true'))
            """, new { userId, puuid, actId });
        if (totals is null) return;
        var matches = (await conn.QueryAsync<ValorantMatchArchive.Match>("""
            SELECT m.match_id AS id, m.agent_id, m.won, m.kills, m.deaths, m.assists, m.score,
                m.headshots, m.shots, m.headshot_data_complete
            FROM public.valorant_player_matches m
            JOIN public.player_game_stats p ON p.user_id = m.user_id AND p.provider_player_id = m.provider_player_id
                AND p.game_slug = 'valorant' AND p.last_act_id = m.act_id
            JOIN public.valorant_player_matches anchor ON anchor.user_id = m.user_id
                AND anchor.provider_player_id = m.provider_player_id AND anchor.match_id = p.last_match_id
                AND anchor.act_id = m.act_id
            WHERE m.user_id = @userId AND m.provider_player_id = @puuid AND m.act_id = @actId
              AND m.queue_id = 'competitive' AND m.match_data_version >= 1
              AND m.started_at <= anchor.started_at
            """, new { userId, puuid, actId })).AsList();
        var recovered = Build(totals, matches);
        var agents = recovered?.PopularAgents ?? RepairAgents(totals.PopularAgents, matches);
        if (recovered is null && agents == totals.PopularAgents) return;
        await conn.ExecuteAsync("""
            UPDATE public.player_game_stats SET
                total_headshots = CASE WHEN @complete THEN @headshots ELSE total_headshots END,
                total_shots = CASE WHEN @complete THEN @shots ELSE total_shots END,
                headshot_data_complete = CASE WHEN @complete THEN TRUE ELSE headshot_data_complete END,
                hs_pct = CASE WHEN @complete THEN @percentage ELSE hs_pct END, popular_agents = @agents::jsonb
            WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
              AND last_act_id = @actId AND EXISTS (SELECT 1 FROM public.riot_accounts ra
                  WHERE ra.user_id = @userId AND ra.puuid = @puuid)
            """, new
        {
            userId,
            puuid,
            actId,
            complete = recovered is not null,
            headshots = recovered?.Headshots ?? 0,
            shots = recovered?.Shots ?? 0,
            percentage = recovered?.Percentage,
            agents
        });
    }

    internal static Recovered? Build(Totals totals, IReadOnlyList<ValorantMatchArchive.Match> matches)
    {
        // A subset cannot establish the aggregate's HS%. Match counts and all
        // independent counters must agree before replacing the hit totals.
        if (totals.Matches <= 0 || matches.Count != totals.Matches ||
            matches.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != matches.Count ||
            matches.Any(m => m.HeadshotDataComplete != true || m.Headshots < 0 || m.Shots < m.Headshots) ||
            matches.Count(m => m.Won == true) != totals.Wins || matches.Count(m => m.Won == false) != totals.Losses ||
            matches.Sum(m => m.Kills) != totals.Kills || matches.Sum(m => m.Deaths) != totals.Deaths ||
            matches.Sum(m => m.Assists) != totals.Assists || matches.Sum(m => m.Score) != totals.Score)
            return null;
        var headshots = matches.Sum(m => m.Headshots);
        var shots = matches.Sum(m => m.Shots);
        return new Recovered(headshots, shots, Percent(headshots, shots), RepairAgents(totals.PopularAgents, matches));
    }

    private static decimal? Percent(int headshots, int shots) => shots > 0 ? Math.Round(100m * headshots / shots, 2) : null;

    internal static string? RepairAgents(string? json, IReadOnlyList<ValorantMatchArchive.Match> matches)
    {
        if (string.IsNullOrEmpty(json)) return json;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray agents) return json;
            var changed = false;
            foreach (var agent in agents.OfType<JsonObject>())
            {
                var id = agent["agent_id"]?.GetValue<string>();
                var games = matches.Where(m => string.Equals(m.AgentId, id, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (games.Length == 0 || agent["games_played"]?.GetValue<int>() != games.Length ||
                    games.Select(game => game.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != games.Length ||
                    games.Any(game => game.HeadshotDataComplete != true || game.Headshots < 0 || game.Shots < game.Headshots) ||
                    agent["wins"]?.GetValue<int>() != games.Count(game => game.Won == true) ||
                    agent["kills"]?.GetValue<int>() != games.Sum(game => game.Kills) ||
                    agent["deaths"]?.GetValue<int>() != games.Sum(game => game.Deaths) ||
                    (agent["assists"] is not null && agent["assists"]!.GetValue<int>() != games.Sum(game => game.Assists))) continue;
                var headshots = games.Sum(m => m.Headshots);
                var shots = games.Sum(m => m.Shots);
                agent["headshots"] = headshots;
                agent["shots"] = shots;
                agent["headshot_data_complete"] = true;
                agent["hs_pct"] = Percent(headshots, shots);
                changed = true;
            }
            return changed ? agents.ToJsonString() : json;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return json; }
    }
}
