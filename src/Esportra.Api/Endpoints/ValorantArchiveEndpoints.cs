using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

internal static class ValorantArchiveEndpoints
{
    internal sealed record ActRow
    {
        public string ActId { get; init; } = "";
        public bool IsCurrent { get; init; }
        public DateTime StartsAt { get; init; }
        public string SyncStatus { get; init; } = "pending";
        public int Matches { get; init; }
        public int CompetitiveMatches { get; init; }
        public string PopularAgents { get; init; } = "[]";
    }

    internal static async Task<object[]> GetActsAsync(IDbConnection conn, Guid userId)
    {
        var rows = await conn.QueryAsync<ActRow>("""
            SELECT a.act_id, a.is_current, a.starts_at, a.sync_status,
                COUNT(m.match_id)::int AS matches,
                COUNT(m.match_id) FILTER (WHERE m.queue_id = 'competitive')::int AS competitive_matches,
                COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'agent_id', s.agent_id, 'games_played', s.games, 'wins', s.wins,
                    'kills', s.kills, 'deaths', s.deaths,
                    'kd_ratio', CASE WHEN s.deaths > 0 THEN round(s.kills::numeric / s.deaths, 2) ELSE NULL END)
                    ORDER BY s.games DESC, s.agent_id)
                  FROM (SELECT vm.agent_id, count(*)::int games, count(*) FILTER (WHERE vm.won)::int wins,
                        sum(vm.kills)::int kills, sum(vm.deaths)::int deaths
                    FROM public.valorant_player_matches vm
                    WHERE vm.user_id = a.user_id AND vm.provider_player_id = a.provider_player_id
                      AND vm.act_id = a.act_id AND vm.queue_id = 'competitive' AND vm.agent_id IS NOT NULL
                    GROUP BY vm.agent_id) s), '[]'::jsonb)::text AS popular_agents
            FROM public.valorant_act_syncs a
            JOIN public.riot_accounts ra ON ra.user_id = a.user_id AND ra.puuid = a.provider_player_id
            LEFT JOIN public.valorant_player_matches m ON m.user_id = a.user_id
              AND m.provider_player_id = a.provider_player_id AND m.act_id = a.act_id
            WHERE a.user_id = @userId
            GROUP BY a.user_id, a.provider_player_id, a.act_id, a.is_current, a.starts_at, a.sync_status
            ORDER BY a.starts_at DESC LIMIT 2
            """, new { userId });
        return rows.Select(row => (object)new
        {
            act_id = row.ActId,
            is_current = row.IsCurrent,
            starts_at = row.StartsAt,
            sync_status = row.SyncStatus,
            matches = row.Matches,
            competitive_matches = row.CompetitiveMatches,
            popular_agents = ParseAgents(row.PopularAgents)
        }).ToArray();
    }

    private static JsonElement ParseAgents(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static async Task<IResult> GetMatchesAsync(HttpContext ctx, IDbConnectionFactory db,
        [FromQuery] string? act, [FromQuery] int limit = 20, [FromQuery] int offset = 0)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (act is not (null or "current" or "previous")) return Results.BadRequest(new { error = "Unknown act." });
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<MatchRow>("""
            SELECT m.match_id, m.act_id, m.started_at, m.queue_id, m.agent_id, m.won,
                   m.kills, m.deaths, m.assists, m.score, m.competitive_tier
            FROM public.valorant_player_matches m
            JOIN public.riot_accounts ra ON ra.user_id = m.user_id AND ra.puuid = m.provider_player_id
            WHERE m.user_id = @userId AND m.act_id = (
              SELECT a.act_id FROM public.valorant_act_syncs a
              WHERE a.user_id = @userId AND a.provider_player_id = ra.puuid
              ORDER BY a.starts_at DESC LIMIT 1 OFFSET @actOffset)
            ORDER BY m.started_at DESC, m.match_id DESC LIMIT @limit OFFSET @offset
            """, new
        {
            userId = user.UserIdGuid,
            actOffset = act == "previous" ? 1 : 0,
            limit = Math.Clamp(limit, 1, 50),
            offset = Math.Max(offset, 0)
        });
        return Results.Ok(rows);
    }

    internal sealed record MatchRow
    {
        public string MatchId { get; init; } = "";
        public string ActId { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public string? QueueId { get; init; }
        public string? AgentId { get; init; }
        public bool? Won { get; init; }
        public int Kills { get; init; }
        public int Deaths { get; init; }
        public int Assists { get; init; }
        public int Score { get; init; }
        public int? CompetitiveTier { get; init; }
    }
}
