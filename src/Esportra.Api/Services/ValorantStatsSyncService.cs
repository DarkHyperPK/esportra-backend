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
                       total_assists, matches_analyzed, total_score
                FROM public.player_game_stats
                WHERE user_id = @userId AND game_slug = 'valorant'
                """,
                new { userId });

            var actReset = existing?.LastActId != act.ActId;
            var lastMatchId = actReset ? null : existing?.LastMatchId;

            var matchIds = ParseCompetitiveMatchIds(listBody, lastMatchId, actReset, act.StartTime);
            if (matchIds.Count == 0)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.player_game_stats
                    SET sync_status = 'complete', synced_at = now(), updated_at = now(),
                        last_act_id = @actId
                    WHERE user_id = @userId AND game_slug = 'valorant'
                    """,
                    new { userId, actId = act.ActId });
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

            string? newestMatchId = null;
            var rateLimited = false;

            foreach (var matchId in matchIds)
            {
                ct.ThrowIfCancellationRequested();

                var (detailStatus, detailBody) = await riot.ProxyAsync(
                    shard, $"/val/match/v1/matches/{Uri.EscapeDataString(matchId)}", ct);

                if (detailStatus is 429 or >= 500)
                {
                    rateLimited = true;
                    break;
                }
                if (detailStatus != 200) continue;

                newestMatchId ??= matchId;

                try
                {
                    using var doc = JsonDocument.Parse(detailBody);
                    ExtractPlayerStats(doc.RootElement, account.Puuid, stats);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "ValorantStatsSync: could not parse match {MatchId}", matchId);
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
                    last_match_id         = COALESCE(@lastMatchId, last_match_id),
                    last_act_id           = @actId,
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
                    actId = act.ActId,
                    status = rateLimited ? "partial" : "complete",
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

    private static List<string> ParseCompetitiveMatchIds(
        string matchlistJson, string? lastKnownMatchId, bool actReset, DateTimeOffset actStart)
    {
        using var doc = JsonDocument.Parse(matchlistJson);
        if (!doc.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array)
            return [];

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

    private static void ExtractPlayerStats(JsonElement root, string puuid, AggregatedStats stats)
    {
        var info = root.GetProperty("matchInfo");
        var queueId = info.TryGetProperty("queueId", out var q) ? q.GetString() : "";
        if (!string.Equals(queueId, "competitive", StringComparison.OrdinalIgnoreCase)) return;

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
            // Most recent match (first iteration) wins for tier — only set once
            if (tier > 0 && stats.LatestCompetitiveTier == 0)
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

            break;
        }
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

                    foreach (var season in data.EnumerateArray())
                    {
                        var type = season.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (!string.Equals(type, "act", StringComparison.OrdinalIgnoreCase)) continue;

                        var isActive = season.TryGetProperty("isActive", out var ia) && ia.GetBoolean();
                        if (!isActive) continue;

                        var uuid = season.TryGetProperty("uuid", out var u) ? u.GetString() : null;
                        var startTime = season.TryGetProperty("startTime", out var st) ? st.GetString() : null;

                        if (uuid is null || startTime is null) continue;
                        if (DateTimeOffset.TryParse(startTime, out var actStart))
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
        int MatchesAnalyzed, int TotalScore);

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
