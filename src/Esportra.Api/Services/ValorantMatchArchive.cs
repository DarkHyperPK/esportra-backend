using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Infrastructure.Integrations;

namespace Esportra.Api.Services;

/// <summary>Stores only the linked player's match fields; raw match responses and other players stay private.</summary>
internal static class ValorantMatchArchive
{
    internal sealed record Act(string Id, DateTimeOffset Start, bool IsCurrent);
    internal sealed record Match
    {
        public string Id { get; init; } = "";
        public string ActId { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public string? QueueId { get; init; }
        public string? AgentId { get; init; }
        public bool? Won { get; init; }
        public int Kills { get; init; }
        public int Deaths { get; init; }
        public int Assists { get; init; }
        public int Score { get; init; }
        public int? Tier { get; init; }
        public int Headshots { get; init; }
        public int Shots { get; init; }
        public decimal Performance { get; init; }
    }

    internal static async Task<bool> SyncAsync(IDbConnection conn, RiotApiClient riot, Guid userId,
        string puuid, string shard, string matchlist, IReadOnlyList<Act> acts, CancellationToken ct)
    {
        if (acts.Count == 0) return false;
        foreach (var act in acts)
            await conn.ExecuteAsync("""
                INSERT INTO public.valorant_act_syncs (user_id, provider_player_id, act_id, starts_at, is_current, sync_status)
                VALUES (@userId, @puuid, @id, @start, @isCurrent, 'pending')
                ON CONFLICT (user_id, provider_player_id, act_id) DO UPDATE SET
                  starts_at = EXCLUDED.starts_at, is_current = EXCLUDED.is_current, sync_status = 'pending', updated_at = now()
                """, new { userId, puuid, id = act.Id, start = act.Start.UtcDateTime, isCurrent = act.IsCurrent });
        await conn.ExecuteAsync("""
            UPDATE public.valorant_act_syncs SET is_current = false
            WHERE user_id = @userId AND provider_player_id = @puuid AND act_id <> @currentId AND is_current
            """, new { userId, puuid, currentId = acts.Single(a => a.IsCurrent).Id });

        var candidates = CandidateEntries(matchlist, acts.Min(a => a.Start));
        await conn.ExecuteAsync("""
            INSERT INTO public.valorant_match_backlog (user_id, provider_player_id, match_id, match_started_at)
            SELECT @userId, @puuid, item.id,
                   CASE WHEN item.start_ms >= 0 THEN to_timestamp(item.start_ms::double precision / 1000) ELSE NULL END
            FROM unnest(@ids::text[], @starts::bigint[]) AS item(id, start_ms)
            WHERE NOT EXISTS (SELECT 1 FROM public.valorant_player_matches m
              WHERE m.user_id = @userId AND m.provider_player_id = @puuid AND m.match_id = item.id)
            ON CONFLICT (user_id, provider_player_id, match_id) DO UPDATE SET
                match_started_at = COALESCE(EXCLUDED.match_started_at, valorant_match_backlog.match_started_at)
            """, new { userId, puuid, ids = candidates.Select(c => c.Id).ToArray(), starts = candidates.Select(c => c.StartMillis).ToArray() });
        var pending = await conn.QueryAsync<string>("""
            SELECT match_id FROM public.valorant_match_backlog
            WHERE user_id = @userId AND provider_player_id = @puuid
            ORDER BY match_started_at DESC NULLS LAST, discovered_at, match_id
            """, new { userId, puuid });
        var complete = true;
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            var (status, body) = await riot.ProxyAsync(shard, $"/val/match/v1/matches/{Uri.EscapeDataString(id)}", ct);
            if (status != 200) { complete = false; if (status is 401 or 403 or 429 or >= 500) break; continue; }
            try
            {
                var match = ParseMatch(body, id, puuid);
                if (match is null) { complete = false; continue; }
                if (!acts.Any(act => string.Equals(act.Id, match.ActId, StringComparison.OrdinalIgnoreCase)))
                {
                    await RemovePendingAsync(conn, userId, puuid, id);
                    continue;
                }
                await conn.ExecuteAsync("""
                    INSERT INTO public.valorant_player_matches
                        (user_id, provider_player_id, match_id, act_id, started_at, queue_id, agent_id, won,
                         kills, deaths, assists, score, competitive_tier, headshots, shots, performance)
                    VALUES (@userId, @puuid, @id, @actId, @startedAt, @queueId, @agentId, @won,
                            @kills, @deaths, @assists, @score, @tier, @headshots, @shots, @performance)
                    ON CONFLICT (user_id, provider_player_id, match_id) DO UPDATE SET
                        headshots   = CASE WHEN valorant_player_matches.shots = 0 THEN EXCLUDED.headshots   ELSE valorant_player_matches.headshots   END,
                        shots       = CASE WHEN valorant_player_matches.shots = 0 THEN EXCLUDED.shots       ELSE valorant_player_matches.shots       END,
                        performance = CASE WHEN valorant_player_matches.shots = 0 THEN EXCLUDED.performance ELSE valorant_player_matches.performance END
                    """, new
                {
                    userId,
                    puuid,
                    match.Id,
                    match.ActId,
                    match.StartedAt,
                    match.QueueId,
                    match.AgentId,
                    match.Won,
                    match.Kills,
                    match.Deaths,
                    match.Assists,
                    match.Score,
                    match.Tier,
                    match.Headshots,
                    match.Shots,
                    match.Performance
                });
                await RemovePendingAsync(conn, userId, puuid, id);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
            { complete = false; }
        }
        await conn.ExecuteAsync("""
            UPDATE public.valorant_act_syncs SET sync_status = @status, updated_at = now()
            WHERE user_id = @userId AND provider_player_id = @puuid AND act_id = ANY(@ids)
            """, new { userId, puuid, ids = acts.Select(a => a.Id).ToArray(), status = complete ? "available" : "partial" });
        return complete && acts.Count == 2;
    }

    private static Task<int> RemovePendingAsync(IDbConnection conn, Guid userId, string puuid, string id) =>
        conn.ExecuteAsync("DELETE FROM public.valorant_match_backlog WHERE user_id = @userId AND provider_player_id = @puuid AND match_id = @id",
            new { userId, puuid, id });

    internal static List<string> CandidateIds(string json, DateTimeOffset earliest) =>
        CandidateEntries(json, earliest).Select(entry => entry.Id).ToList();

    private static List<(string Id, long StartMillis)> CandidateEntries(string json, DateTimeOffset earliest)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array)
            throw new JsonException("Match history is missing or invalid.");
        var entries = new List<(string, long)>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in history.EnumerateArray())
        {
            if (!item.TryGetProperty("matchId", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var ms = -1L;
            if (item.TryGetProperty("gameStartTimeMillis", out var start) && start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out var parsed))
                ms = parsed;
            if (ms >= 0 && ms < earliest.ToUnixTimeMilliseconds()) continue;
            var value = id.GetString();
            if (!string.IsNullOrWhiteSpace(value) && ids.Add(value)) entries.Add((value, ms));
        }
        return entries;
    }

    internal static Match? ParseMatch(string json, string expectedId, string puuid)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = root.GetProperty("matchInfo");
        if (!string.Equals(info.GetProperty("matchId").GetString(), expectedId, StringComparison.OrdinalIgnoreCase)) return null;
        var act = info.GetProperty("seasonId").GetString();
        if (string.IsNullOrWhiteSpace(act)) return null;
        var started = DateTimeOffset.FromUnixTimeMilliseconds(info.GetProperty("gameStartMillis").GetInt64()).UtcDateTime;
        var player = root.GetProperty("players").EnumerateArray().FirstOrDefault(p => p.GetProperty("puuid").GetString() == puuid);
        if (player.ValueKind == JsonValueKind.Undefined) return null;
        var stats = player.GetProperty("stats");
        var teamId = player.GetProperty("teamId").GetString();
        var teams = root.GetProperty("teams").EnumerateArray().ToArray();
        var team = teams.FirstOrDefault(t => t.GetProperty("teamId").GetString() == teamId);
        var anyWinner = teams.Any(t => t.TryGetProperty("won", out var won) && won.ValueKind == JsonValueKind.True);
        bool? won = anyWinner && team.ValueKind != JsonValueKind.Undefined && team.TryGetProperty("won", out var result)
            && result.ValueKind is JsonValueKind.True or JsonValueKind.False ? result.GetBoolean() : null;

        var (headshots, shots) = ExtractHeadshots(root, puuid);
        var performance = stats.TryGetProperty("performance", out var perf) && perf.TryGetDouble(out var perfVal)
            ? (decimal)perfVal : 0m;

        return new Match
        {
            Id = expectedId,
            ActId = act,
            StartedAt = started,
            QueueId = info.TryGetProperty("queueId", out var queue) ? queue.GetString() : null,
            AgentId = player.TryGetProperty("characterId", out var agent) ? agent.GetString() : null,
            Won = won,
            Kills = stats.GetProperty("kills").GetInt32(),
            Deaths = stats.GetProperty("deaths").GetInt32(),
            Assists = stats.GetProperty("assists").GetInt32(),
            Score = stats.GetProperty("score").GetInt32(),
            Tier = player.TryGetProperty("competitiveTier", out var tier) && tier.TryGetInt32(out var value) && value >= 3 ? value : null,
            Headshots = headshots,
            Shots = shots,
            Performance = performance
        };
    }

    internal static (int Headshots, int Shots) ExtractHeadshots(JsonElement root, string puuid)
    {
        var headshots = 0;
        var shots = 0;
        if (!root.TryGetProperty("roundResults", out var rounds) || rounds.ValueKind != JsonValueKind.Array)
            return (0, 0);
        foreach (var round in rounds.EnumerateArray())
        {
            if (!round.TryGetProperty("playerStats", out var pStats) || pStats.ValueKind != JsonValueKind.Array) continue;
            foreach (var prs in pStats.EnumerateArray())
            {
                if (!prs.TryGetProperty("puuid", out var pid) ||
                    !string.Equals(pid.GetString(), puuid, StringComparison.Ordinal)) continue;
                if (!prs.TryGetProperty("damage", out var dmgs) || dmgs.ValueKind != JsonValueKind.Array) break;
                foreach (var dmg in dmgs.EnumerateArray())
                {
                    var hs = dmg.TryGetProperty("headshots", out var h) && h.TryGetInt32(out var hv) ? hv : 0;
                    var bs = dmg.TryGetProperty("bodyshots", out var b) && b.TryGetInt32(out var bv) ? bv : 0;
                    var ls = dmg.TryGetProperty("legshots", out var l) && l.TryGetInt32(out var lv) ? lv : 0;
                    headshots += hs;
                    shots += hs + bs + ls;
                }
                break;
            }
        }
        return (headshots, shots);
    }
}
