using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Integrations;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

public sealed class ValorantStatsSyncService(
    RiotApiClient riot,
    HybridCache cache,
    IDbConnectionFactory db,
    IHttpClientFactory httpFactory,
    ILogger<ValorantStatsSyncService> logger)
{
    private const string ValorantAgentsUrl = "https://valorant-api.com/v1/agents?isPlayableCharacter=true";
    private const string ValorantSeasonsUrl = "https://valorant-api.com/v1/seasons";
    private const int MaxMatchesPerAct = 500; // safety cap — a single act rarely exceeds 200 ranked games

    public async Task SyncForUserAsync(Guid userId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var locked = await conn.ExecuteScalarAsync<bool>(
            "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", new { key = $"valorant-stats:{userId}" });
        if (!locked) return;
        try
        {

            var account = await conn.QuerySingleOrDefaultAsync<(string Puuid, string? Region)>(
                "SELECT puuid, region FROM public.riot_accounts WHERE user_id = @userId",
                new { userId });

            if (account.Puuid is null)
            {
                logger.LogDebug("ValorantStatsSync: user {UserId} has no linked Riot account", userId);
                return;
            }

            await conn.ExecuteAsync(
                """
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status)
            VALUES (@userId, 'valorant', @puuid, 'syncing')
            ON CONFLICT (user_id, game_slug) DO UPDATE SET
                sync_status = 'syncing', updated_at = now()
            """,
                new { userId, puuid = account.Puuid });

            try
            {
                var act = await GetCurrentActAsync(ct);
                if (act.ActId == "unknown")
                {
                    await MarkError(conn, userId, "Current Valorant act is unavailable; try again later.");
                    return;
                }
                var shard = await RiotShards.ResolveAsync(riot, cache, account.Puuid, account.Region, ct);
                var (listStatus, listBody) = await riot.ProxyAsync(
                    shard, $"/val/match/v1/matchlists/by-puuid/{Uri.EscapeDataString(account.Puuid)}", ct);

                if (listStatus is 429 or >= 500)
                {
                    await MarkError(conn, userId, "Rate limited or Riot API unavailable");
                    return;
                }
                if (listStatus is 401 or 403)
                {
                    await MarkError(conn, userId, "API key unauthorized for match data");
                    return;
                }
                if (listStatus != 200)
                {
                    await MarkError(conn, userId, $"Matchlist fetch returned {listStatus}");
                    return;
                }

                var existing = await conn.QuerySingleOrDefaultAsync<ExistingPgsStats?>(
                    """
                SELECT last_match_id, last_act_id,
                       win_count, loss_count, total_kills, total_deaths,
                       total_assists, matches_analyzed, total_score, popular_agents::text AS popular_agents,
                       aggregation_version
                FROM public.player_game_stats
                WHERE user_id = @userId AND game_slug = 'valorant'
                """,
                    new { userId });

                var actReset = existing?.LastActId != act.ActId;
                // Version upgrades advance the counter in the UPDATE without wiping totals —
                // rebuilding from Riot history cannot guarantee completeness.
                var lastMatchId = actReset ? null : existing?.LastMatchId;
                var rebuilding = false;

                if (lastMatchId is not null && !MatchlistContains(listBody, lastMatchId))
                {
                    // Cursor match rotated out of Riot's history window — rebuild from available matches
                    logger.LogWarning(
                        "ValorantStatsSync: cursor {MatchId} missing for user {UserId} — rebuilding act from available history",
                        lastMatchId, userId);
                    actReset = true;
                    lastMatchId = null;
                    rebuilding = true;
                }

                var matchIds = ParseCompetitiveMatchIds(listBody, lastMatchId, actReset, act.StartTime);
                if (matchIds.Count == MaxMatchesPerAct)
                {
                    await MarkError(conn, userId, "Too many new matches to sync safely; stats require a rebuild.");
                    return;
                }
                if (matchIds.Count == 0)
                {
                    await conn.ExecuteAsync(
                        """
                    UPDATE public.player_game_stats SET
                        win_count = CASE WHEN @reset THEN 0 ELSE win_count END,
                        loss_count = CASE WHEN @reset THEN 0 ELSE loss_count END,
                        total_kills = CASE WHEN @reset THEN 0 ELSE total_kills END,
                        total_deaths = CASE WHEN @reset THEN 0 ELSE total_deaths END,
                        total_assists = CASE WHEN @reset THEN 0 ELSE total_assists END,
                        matches_analyzed = CASE WHEN @reset THEN 0 ELSE matches_analyzed END,
                        total_score = CASE WHEN @reset THEN 0 ELSE total_score END,
                        popular_agents = CASE WHEN @reset THEN '[]'::jsonb ELSE popular_agents END,
                        win_rate = CASE WHEN @reset THEN NULL ELSE win_rate END,
                        kd_ratio = CASE WHEN @reset THEN NULL ELSE kd_ratio END,
                        avg_score = CASE WHEN @reset THEN NULL ELSE avg_score END,
                        last_match_id = CASE WHEN @reset THEN NULL ELSE last_match_id END,
                        aggregation_version = 1, sync_status = 'complete', synced_at = now(), updated_at = now(), last_act_id = @actId
                    WHERE user_id = @userId AND game_slug = 'valorant'
                    """,
                        new { userId, actId = act.ActId, reset = actReset });
                    return;
                }

                var stats = actReset
                    ? new AggregatedStats()
                    : new AggregatedStats
                    {
                        Wins = existing?.WinCount ?? 0,
                        Losses = existing?.LossCount ?? 0,
                        TotalMatches = existing?.MatchesAnalyzed ?? 0,
                        Kills = existing?.TotalKills ?? 0,
                        Deaths = existing?.TotalDeaths ?? 0,
                        Assists = existing?.TotalAssists ?? 0,
                        TotalScore = existing?.TotalScore ?? 0,
                    };

                if (!actReset && existing is not null) RestoreAgentStats(existing.PopularAgents, stats.AgentStats);

                string? newestMatchId = null;
                var rateLimited = false;

                foreach (var matchId in matchIds.AsEnumerable().Reverse())
                {
                    ct.ThrowIfCancellationRequested();

                    var (detailStatus, detailBody) = await riot.ProxyAsync(
                        shard, $"/val/match/v1/matches/{Uri.EscapeDataString(matchId)}", ct);

                    if (detailStatus is 429 or >= 500)
                    {
                        rateLimited = true;
                        break;
                    }
                    if (detailStatus != 200) { rateLimited = true; break; }

                    try
                    {
                        using var doc = JsonDocument.Parse(detailBody);
                        if (!ExtractPlayerStats(doc.RootElement, account.Puuid, stats)) { rateLimited = true; break; }
                        newestMatchId = matchId;
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        logger.LogWarning(ex, "ValorantStatsSync: could not parse match {MatchId}", matchId);
                        rateLimited = true;
                        break;
                    }
                }

                var agentMap = await GetAgentMapAsync(ct);
                var popularAgents = BuildPopularAgentsJson(stats.AgentStats, agentMap);
                var winRate = stats.TotalMatches > 0 ? Math.Round(100.0m * stats.Wins / stats.TotalMatches, 2) : (decimal?)null;
                var kdRatio = stats.Deaths > 0 ? Math.Round((decimal)stats.Kills / stats.Deaths, 2) : (decimal?)null;
                var avgScore = stats.TotalMatches > 0 ? stats.TotalScore / stats.TotalMatches : (int?)null;

                var tierName = stats.LatestCompetitiveTier > 0
                    ? await ResolveTierName(conn, stats.LatestCompetitiveTier)
                    : null;

                await conn.ExecuteAsync(
                    """
                UPDATE public.player_game_stats SET
                    competitive_tier      = COALESCE(@tier, competitive_tier),
                    competitive_tier_name = COALESCE(@tierName, competitive_tier_name),
                    win_count             = @wins,
                    loss_count            = @losses,
                    total_kills           = @kills,
                    total_deaths          = @deaths,
                    total_assists         = @assists,
                    matches_analyzed      = @matches,
                    popular_agents        = @agents::jsonb,
                    win_rate              = @winRate,
                    kd_ratio              = @kdRatio,
                    avg_score             = @avgScore,
                    total_score           = @totalScore,
                    last_match_id         = CASE WHEN @reset THEN @lastMatchId ELSE COALESCE(@lastMatchId, last_match_id) END,
                    last_act_id           = @actId,
                    aggregation_version   = 1,
                    synced_at             = now(),
                    sync_status           = @status,
                    sync_error            = NULL,
                    updated_at            = now()
                WHERE user_id = @userId AND game_slug = 'valorant'
                """,
                    new
                    {
                        userId,
                        tier = stats.LatestCompetitiveTier > 0 ? stats.LatestCompetitiveTier : (int?)null,
                        tierName,
                        wins = stats.Wins,
                        losses = stats.Losses,
                        kills = stats.Kills,
                        deaths = stats.Deaths,
                        assists = stats.Assists,
                        matches = stats.TotalMatches,
                        agents = popularAgents,
                        winRate,
                        kdRatio,
                        avgScore,
                        totalScore = stats.TotalScore,
                        lastMatchId = newestMatchId,
                        reset = actReset,
                        actId = act.ActId,
                        status = rateLimited || rebuilding ? "partial" : "complete",
                    });

                logger.LogInformation(
                    "ValorantStatsSync: synced {New} new matches (act total {Total}) for user {UserId} — tier {Tier}, {WinRate}% WR, act {ActId}",
                    matchIds.Count, stats.TotalMatches, userId, stats.LatestCompetitiveTier, winRate, act.ActId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ValorantStatsSync: failed for user {UserId}", userId);
                await MarkError(conn, userId, ex.Message);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key = $"valorant-stats:{userId}" });
        }
    }

    private static bool MatchlistContains(string json, string matchId)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array &&
            history.EnumerateArray().Any(entry => entry.TryGetProperty("matchId", out var id) && id.GetString() == matchId);
    }

    private static void RestoreAgentStats(string? json, Dictionary<string, AgentStat> target)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("agent_id").GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            target[id] = new AgentStat
            {
                CharacterId = id,
                GamesPlayed = item.GetProperty("games_played").GetInt32(),
                Wins = item.GetProperty("wins").GetInt32(),
                Kills = item.GetProperty("kills").GetInt32(),
                Deaths = item.GetProperty("deaths").GetInt32()
            };
        }
    }

    private static List<string> ParseCompetitiveMatchIds(
        string matchlistJson, string? lastKnownMatchId, bool actReset, DateTimeOffset actStart)
    {
        using var doc = JsonDocument.Parse(matchlistJson);
        if (!doc.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array)
            throw new JsonException("Match history is missing or invalid.");

        var actStartMs = actStart.ToUnixTimeMilliseconds();
        var ids = new List<string>();

        foreach (var entry in history.EnumerateArray())
        {
            if (ids.Count >= MaxMatchesPerAct) break;

            var matchId = entry.TryGetProperty("matchId", out var mid) ? mid.GetString() : null;
            if (matchId is null) continue;

            // Incremental: stop when we reach the last already-processed match
            if (!actReset && matchId == lastKnownMatchId) break;

            var queueId = entry.TryGetProperty("queueId", out var q) ? q.GetString() : null;
            if (!string.Equals(queueId, "competitive", StringComparison.OrdinalIgnoreCase)) continue;

            // Stop once we go before the current act's start (matchlist is newest-first)
            if (entry.TryGetProperty("gameStartTimeMillis", out var gst) &&
                gst.TryGetInt64(out var startMs) && startMs < actStartMs)
                break;

            ids.Add(matchId);
        }

        return ids;
    }

    private static bool ExtractPlayerStats(JsonElement root, string puuid, AggregatedStats stats)
    {
        var info = root.GetProperty("matchInfo");
        var queueId = info.TryGetProperty("queueId", out var q) ? q.GetString() : "";
        if (!string.Equals(queueId, "competitive", StringComparison.OrdinalIgnoreCase)) return false;

        var teams = root.GetProperty("teams");
        var teamWins = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in teams.EnumerateArray())
        {
            var teamId = team.GetProperty("teamId").GetString() ?? "";
            var won = team.TryGetProperty("won", out var w) && w.GetBoolean();
            teamWins[teamId] = won;
        }

        foreach (var player in root.GetProperty("players").EnumerateArray())
        {
            var pPuuid = player.GetProperty("puuid").GetString() ?? "";
            if (!string.Equals(pPuuid, puuid, StringComparison.Ordinal)) continue;

            var playerStats = player.GetProperty("stats");
            var kills = playerStats.GetProperty("kills").GetInt32();
            var deaths = playerStats.GetProperty("deaths").GetInt32();
            var assists = playerStats.GetProperty("assists").GetInt32();
            var score = playerStats.GetProperty("score").GetInt32();
            var characterId = player.GetProperty("characterId").GetString() ?? "";
            var playerTeamId = player.TryGetProperty("teamId", out var tid) ? tid.GetString() ?? "" : "";
            var tier = player.TryGetProperty("competitiveTier", out var ct) && ct.TryGetInt32(out var tierVal)
                ? tierVal : 0;

            var won = teamWins.TryGetValue(playerTeamId, out var w) && w;

            stats.TotalMatches++;
            stats.Kills += kills;
            stats.Deaths += deaths;
            stats.Assists += assists;
            stats.TotalScore += score;
            if (won) stats.Wins++; else stats.Losses++;
            // Matches are processed oldest-first; the latest available tier wins.
            if (tier > 0)
                stats.LatestCompetitiveTier = tier;

            if (!string.IsNullOrEmpty(characterId))
            {
                if (!stats.AgentStats.TryGetValue(characterId, out var agentStat))
                {
                    agentStat = new AgentStat { CharacterId = characterId };
                    stats.AgentStats[characterId] = agentStat;
                }
                agentStat.GamesPlayed++;
                agentStat.Kills += kills;
                agentStat.Deaths += deaths;
                if (won) agentStat.Wins++;
            }

            return true;
        }
        return false;
    }

    private static async Task<string?> ResolveTierName(System.Data.IDbConnection conn, int competitiveTier)
    {
        var sortOrder = competitiveTier - 3;
        return await conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT display_name FROM public.game_catalog_rank_tiers
            WHERE game_slug = 'valorant' AND sort_order = @sortOrder
            LIMIT 1
            """,
            new { sortOrder });
    }

    private async Task<ActInfo> GetCurrentActAsync(CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(
            "valorant:current-act",
            async _ =>
            {
                try
                {
                    var client = httpFactory.CreateClient();
                    using var resp = await client.GetAsync(ValorantSeasonsUrl, ct);
                    if (!resp.IsSuccessStatusCode) return FallbackAct();

                    var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (!doc.RootElement.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Array)
                        return FallbackAct();

                    var now = DateTimeOffset.UtcNow;
                    foreach (var season in data.EnumerateArray())
                    {
                        var type = season.TryGetProperty("type", out var t) ? t.GetString() : null;
                        // valorant-api.com returns "EAresSeasonType::Act"; there is no isActive field
                        if (type is null || !type.EndsWith("::Act", StringComparison.OrdinalIgnoreCase)) continue;

                        var uuid = season.TryGetProperty("uuid", out var u) ? u.GetString() : null;
                        var startTimeStr = season.TryGetProperty("startTime", out var st) ? st.GetString() : null;
                        var endTimeStr = season.TryGetProperty("endTime", out var et) ? et.GetString() : null;

                        if (uuid is null || startTimeStr is null || endTimeStr is null) continue;
                        if (!DateTimeOffset.TryParse(startTimeStr, out var actStart)) continue;
                        if (!DateTimeOffset.TryParse(endTimeStr, out var actEnd)) continue;

                        if (actStart <= now && now < actEnd)
                            return new ActInfo(uuid, actStart);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ValorantStatsSync: could not fetch season data, using fallback");
                }

                return FallbackAct();
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(24) },
            cancellationToken: ct) ?? FallbackAct();

        static ActInfo FallbackAct() => new("unknown", DateTimeOffset.UtcNow.AddMonths(-2));
    }

    private async Task<Dictionary<string, (string Name, string IconUrl)>> GetAgentMapAsync(CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(
            "valorant:agent-map",
            async _ =>
            {
                var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var client = httpFactory.CreateClient();
                    using var response = await client.GetAsync(ValorantAgentsUrl, ct);
                    if (!response.IsSuccessStatusCode) return map;

                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (!doc.RootElement.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Array)
                        return map;

                    foreach (var agent in data.EnumerateArray())
                    {
                        var uuid = agent.TryGetProperty("uuid", out var u) ? u.GetString() : null;
                        var displayName = agent.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                        var displayIcon = agent.TryGetProperty("displayIcon", out var di) ? di.GetString() : null;

                        if (uuid is null || displayName is null || displayIcon is null) continue;
                        map.TryAdd(uuid.ToLowerInvariant(), (displayName, displayIcon));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ValorantStatsSync: could not fetch agent map from valorant-api.com");
                }
                return map;
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(6) },
            cancellationToken: ct) ?? [];
    }

    private static string BuildPopularAgentsJson(
        Dictionary<string, AgentStat> agentStats,
        Dictionary<string, (string Name, string IconUrl)> agentMap)
    {
        var top = agentStats.Values
            .OrderByDescending(a => a.GamesPlayed)
            .ThenByDescending(a => a.Wins)
            .Select(a =>
            {
                var (name, iconUrl) = agentMap.TryGetValue(a.CharacterId, out var info)
                    ? info
                    : (a.CharacterId, "");
                var agentKd = a.Deaths > 0 ? Math.Round((decimal)a.Kills / a.Deaths, 2) : (decimal?)null;
                return new
                {
                    agent_id = a.CharacterId,
                    display_name = name,
                    icon_url = iconUrl,
                    games_played = a.GamesPlayed,
                    wins = a.Wins,
                    kills = a.Kills,
                    deaths = a.Deaths,
                    kd_ratio = agentKd,
                };
            })
            .ToList();

        return JsonSerializer.Serialize(top);
    }

    private static async Task MarkError(System.Data.IDbConnection conn, Guid userId, string error)
    {
        await conn.ExecuteAsync(
            """
            UPDATE public.player_game_stats
            SET sync_status = 'error', sync_error = @error, updated_at = now()
            WHERE user_id = @userId AND game_slug = 'valorant'
            """,
            new { userId, error });
    }

    private sealed record ActInfo(string ActId, DateTimeOffset StartTime);

    private sealed record ExistingPgsStats(
        string? LastMatchId, string? LastActId,
        int WinCount, int LossCount,
        int TotalKills, int TotalDeaths, int TotalAssists,
        int MatchesAnalyzed, int TotalScore, string? PopularAgents, int AggregationVersion);

    private sealed class AggregatedStats
    {
        public int TotalMatches;
        public int Wins;
        public int Losses;
        public int Kills;
        public int Deaths;
        public int Assists;
        public int TotalScore;
        public int LatestCompetitiveTier;
        public Dictionary<string, AgentStat> AgentStats = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AgentStat
    {
        public required string CharacterId { get; init; }
        public int GamesPlayed;
        public int Wins;
        public int Kills;
        public int Deaths;
    }
}
