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
        public bool? HeadshotDataComplete { get; init; }
        public string? PlayerCardId { get; init; }
        public int MatchDataVersion { get; init; }
    }

    internal static async Task<bool> SyncAsync(IDbConnection conn, RiotApiClient riot, Guid userId,
        string puuid, string shard, string matchlist, IReadOnlyList<Act> acts, CancellationToken ct)
    {
        var current = acts.SingleOrDefault(act => act.IsCurrent);
        if (current is null) return false;
        acts = [current];
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
        // Retry the newest cached match once if older syncs did not store its card.
        var cardCandidate = candidates.OrderByDescending(candidate => candidate.StartMillis).FirstOrDefault().Id;
        await conn.ExecuteAsync("""
            INSERT INTO public.valorant_match_backlog (user_id, provider_player_id, match_id, match_started_at)
            SELECT @userId, @puuid, item.id,
                   CASE WHEN item.start_ms >= 0 THEN to_timestamp(item.start_ms::double precision / 1000) ELSE NULL END
            FROM unnest(@ids::text[], @starts::bigint[]) AS item(id, start_ms)
            WHERE NOT EXISTS (SELECT 1 FROM public.valorant_player_matches m
              WHERE m.user_id = @userId AND m.provider_player_id = @puuid AND m.match_id = item.id AND m.match_data_version >= 1
                AND (m.player_card_id IS NOT NULL OR m.match_data_version >= 2 OR item.id <> @cardCandidate))
            ON CONFLICT (user_id, provider_player_id, match_id) DO UPDATE SET
                match_started_at = COALESCE(EXCLUDED.match_started_at, valorant_match_backlog.match_started_at)
            """, new { userId, puuid, ids = candidates.Select(c => c.Id).ToArray(), starts = candidates.Select(c => c.StartMillis).ToArray(), cardCandidate });
        // Repair legacy cached competitive matches even if Riot no longer lists
        // their IDs. Successful-but-incomplete responses get a bounded retry below.
        await conn.ExecuteAsync("""
            INSERT INTO public.valorant_match_backlog (user_id, provider_player_id, match_id, match_started_at)
            SELECT user_id, provider_player_id, match_id, started_at FROM public.valorant_player_matches
            WHERE user_id = @userId AND provider_player_id = @puuid AND act_id = @actId
              AND queue_id = 'competitive' AND headshot_data_complete IS NOT TRUE AND match_data_version < 3
            ON CONFLICT (user_id, provider_player_id, match_id) DO NOTHING
            """, new { userId, puuid, actId = current.Id });
        var pending = await conn.QueryAsync<string>("""
            SELECT match_id FROM public.valorant_match_backlog
            WHERE user_id = @userId AND provider_player_id = @puuid
              AND (match_started_at IS NULL OR match_started_at >= @start)
              AND (retry_after IS NULL OR retry_after <= now())
            ORDER BY match_started_at DESC NULLS LAST, discovered_at, match_id
            """, new { userId, puuid, start = current.Start.UtcDateTime });
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
                         kills, deaths, assists, score, competitive_tier, headshots, shots,
                         headshot_data_complete, player_card_id, match_data_version)
                    VALUES (@userId, @puuid, @id, @actId, @startedAt, @queueId, @agentId, @won,
                            @kills, @deaths, @assists, @score, @tier, @headshots, @shots,
                            @headshotDataComplete, @playerCardId, 3)
                    ON CONFLICT (user_id, provider_player_id, match_id) DO UPDATE SET
                        act_id = EXCLUDED.act_id, started_at = EXCLUDED.started_at,
                        queue_id = COALESCE(EXCLUDED.queue_id, valorant_player_matches.queue_id),
                        agent_id = COALESCE(EXCLUDED.agent_id, valorant_player_matches.agent_id),
                        won = COALESCE(EXCLUDED.won, valorant_player_matches.won),
                        kills = EXCLUDED.kills, deaths = EXCLUDED.deaths, assists = EXCLUDED.assists,
                        score = EXCLUDED.score,
                        competitive_tier = COALESCE(EXCLUDED.competitive_tier, valorant_player_matches.competitive_tier),
                        headshots = CASE WHEN valorant_player_matches.headshot_data_complete IS TRUE
                            AND EXCLUDED.headshot_data_complete IS NOT TRUE THEN valorant_player_matches.headshots ELSE EXCLUDED.headshots END,
                        shots = CASE WHEN valorant_player_matches.headshot_data_complete IS TRUE
                            AND EXCLUDED.headshot_data_complete IS NOT TRUE THEN valorant_player_matches.shots ELSE EXCLUDED.shots END,
                        headshot_data_complete = CASE WHEN valorant_player_matches.headshot_data_complete IS TRUE
                            THEN true ELSE EXCLUDED.headshot_data_complete END,
                        player_card_id = COALESCE(EXCLUDED.player_card_id, valorant_player_matches.player_card_id),
                        match_data_version = 3
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
                    match.HeadshotDataComplete,
                    match.PlayerCardId
                });
                if (string.Equals(match.QueueId, "competitive", StringComparison.OrdinalIgnoreCase) && match.HeadshotDataComplete != true)
                    await conn.ExecuteAsync("""
                        UPDATE public.valorant_match_backlog SET retry_after = now() + interval '12 hours'
                        WHERE user_id = @userId AND provider_player_id = @puuid AND match_id = @id
                        """, new { userId, puuid, id });
                else
                    await RemovePendingAsync(conn, userId, puuid, id);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
            { complete = false; }
        }
        complete &= !await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM public.valorant_match_backlog
                WHERE user_id = @userId AND provider_player_id = @puuid
                  AND (match_started_at IS NULL OR match_started_at >= @start))
            """, new { userId, puuid, start = current.Start.UtcDateTime });
        await conn.ExecuteAsync("""
            UPDATE public.valorant_act_syncs SET sync_status = @status, updated_at = now()
            WHERE user_id = @userId AND provider_player_id = @puuid AND act_id = ANY(@ids)
            """, new { userId, puuid, ids = acts.Select(a => a.Id).ToArray(), status = complete ? "available" : "partial" });
        return complete;
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

        var (headshots, shots, complete) = ExtractHeadshotData(root, puuid);

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
            HeadshotDataComplete = complete,
            PlayerCardId = player.TryGetProperty("playerCard", out var card) && card.ValueKind == JsonValueKind.String ? card.GetString() : null,
            MatchDataVersion = 3
        };
    }

    internal static (int Headshots, int Shots) ExtractHeadshots(JsonElement root, string puuid)
    {
        var (headshots, shots, _) = ExtractHeadshotData(root, puuid);
        return (headshots, shots);
    }

    internal static (int Headshots, int Shots, bool Complete) ExtractHeadshotData(JsonElement root, string puuid)
    {
        var headshots = 0;
        var shots = 0;
        if (!root.TryGetProperty("roundResults", out var rounds) || rounds.ValueKind != JsonValueKind.Array)
            return (0, 0, false);
        var complete = rounds.GetArrayLength() > 0;
        foreach (var round in rounds.EnumerateArray())
        {
            if (!round.TryGetProperty("playerStats", out var pStats) || pStats.ValueKind != JsonValueKind.Array) { complete = false; continue; }
            var foundPlayer = false;
            foreach (var prs in pStats.EnumerateArray())
            {
                if (!prs.TryGetProperty("puuid", out var pid) ||
                    !string.Equals(pid.GetString(), puuid, StringComparison.Ordinal)) continue;
                foundPlayer = true;
                if (!prs.TryGetProperty("damage", out var dmgs) || dmgs.ValueKind != JsonValueKind.Array) { complete = false; break; }
                foreach (var dmg in dmgs.EnumerateArray())
                {
                    if (!dmg.TryGetProperty("headshots", out var h) || h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out var hs) ||
                        !dmg.TryGetProperty("bodyshots", out var b) || b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out var bs) ||
                        !dmg.TryGetProperty("legshots", out var l) || l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out var ls))
                    { complete = false; continue; }
                    if (hs < 0 || bs < 0 || ls < 0) { complete = false; continue; }
                    headshots += hs;
                    shots += hs + bs + ls;
                }
                break;
            }
            if (!foundPlayer) complete = false;
        }
        return (headshots, shots, complete);
    }
}
