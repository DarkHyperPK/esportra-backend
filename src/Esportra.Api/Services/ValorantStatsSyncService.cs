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

    public Task SyncForUserAsync(Guid userId, CancellationToken ct) => SyncForUserAsync(userId, false, ct);

    public async Task SyncForUserAsync(Guid userId, bool retryIncomplete, CancellationToken ct)
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

            var requestedAt = await BeginRefreshAsync(conn, userId, account.Puuid, ct);
            if (requestedAt is null) return;

            using (var tx = conn.BeginTransaction())
            {
                await conn.ExecuteAsync("""
                    INSERT INTO public.valorant_aggregate_snapshots (user_id, provider_player_id, act_id, snapshot)
                    SELECT user_id, provider_player_id, COALESCE(last_act_id, ''), to_jsonb(pgs)
                    FROM public.player_game_stats pgs
                    WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id <> @puuid
                    ON CONFLICT (user_id, provider_player_id, act_id) DO NOTHING
                    """, new { userId, puuid = account.Puuid }, tx);
                await conn.ExecuteAsync("""
                    DELETE FROM public.player_game_stats
                    WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id <> @puuid
                    """, new { userId, puuid = account.Puuid }, tx);
                await conn.ExecuteAsync("""
                    INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status, sync_requested_at, synced_at)
                    VALUES (@userId, 'valorant', @puuid, 'syncing', @requestedAt, NULL)
                    ON CONFLICT (user_id, game_slug) DO UPDATE SET sync_status = 'syncing',
                        sync_requested_at = @requestedAt, updated_at = now()
                    """, new { userId, puuid = account.Puuid, requestedAt }, tx);
                tx.Commit();
            }

            try
            {
                var act = await GetCurrentActAsync(ct);
                if (act.ActId == "unknown")
                {
                    await MarkError(conn, userId, "Current Valorant act is unavailable; try again later.");
                    return;
                }
                using (var tx = conn.BeginTransaction())
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO public.valorant_aggregate_snapshots (user_id, provider_player_id, act_id, snapshot)
                        SELECT user_id, provider_player_id, last_act_id, to_jsonb(pgs)
                        FROM public.player_game_stats pgs
                        WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
                          AND last_act_id IS NOT NULL AND last_act_id <> @actId
                        ON CONFLICT (user_id, provider_player_id, act_id) DO NOTHING
                        """, new { userId, puuid = account.Puuid, actId = act.ActId }, tx);
                    await conn.ExecuteAsync("""
                        UPDATE public.player_game_stats SET competitive_tier = NULL, competitive_tier_name = NULL,
                            win_count = 0, loss_count = 0, total_kills = 0, total_deaths = 0, total_assists = 0,
                            matches_analyzed = 0, total_score = 0, popular_agents = '[]'::jsonb,
                            win_rate = NULL, kd_ratio = NULL, avg_score = NULL, total_headshots = 0,
                            total_shots = 0, headshot_data_complete = true, hs_pct = NULL,
                            last_match_id = NULL, last_act_id = @actId, aggregation_version = 4
                        WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
                          AND last_act_id IS NOT NULL AND last_act_id <> @actId
                        """, new { userId, puuid = account.Puuid, actId = act.ActId }, tx);
                    tx.Commit();
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

                var acts = new List<ValorantMatchArchive.Act> { new(act.ActId, act.StartTime, true) };
                var archiveComplete = false;
                try { archiveComplete = await ValorantMatchArchive.SyncAsync(conn, riot, userId, account.Puuid, shard, listBody, acts, ct, retryIncomplete); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning(ex, "Valorant match archive could not complete for user {UserId}", userId); }

                await ValorantHeadshotRecovery.RepairAsync(conn, userId, account.Puuid, act.ActId);

                // Player cards are account cosmetics, so the latest match in any mode can supply one.
                var archivePlayerCard = await conn.QuerySingleOrDefaultAsync<string>("""
                    SELECT player_card_id FROM public.valorant_player_matches
                    WHERE user_id = @userId AND provider_player_id = @puuid AND act_id = @actId
                      AND player_card_id IS NOT NULL AND player_card_id <> ''
                    ORDER BY started_at DESC, match_id DESC LIMIT 1
                    """, new { userId, puuid = account.Puuid, actId = act.ActId });
                if (archivePlayerCard is not null)
                    await conn.ExecuteAsync("""
                        UPDATE public.player_game_stats SET player_card_id = @archivePlayerCard
                        WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
                          AND EXISTS (SELECT 1 FROM public.riot_accounts ra WHERE ra.user_id = @userId AND ra.puuid = @puuid)
                        """, new { userId, puuid = account.Puuid, archivePlayerCard });

                var existing = await conn.QuerySingleOrDefaultAsync<ExistingPgsStats?>(
                    """
                SELECT last_match_id, last_act_id,
                       win_count, loss_count, total_kills, total_deaths,
                       total_assists, matches_analyzed, total_score, popular_agents::text AS popular_agents,
                       aggregation_version, sync_status,
                       total_headshots, total_shots, headshot_data_complete
                FROM public.player_game_stats
                WHERE user_id = @userId AND game_slug = 'valorant'
                """,
                    new { userId });

                var actReset = existing?.LastActId != act.ActId;
                var coversActStart = HistoryCoversActStart(listBody, act.StartTime);
                // Rebuild legacy aggregates only when the current archive covers the old cursor and totals.
                var archivedCount = await conn.ExecuteScalarAsync<int>("""
                    SELECT count(*)::int FROM public.valorant_player_matches
                    WHERE user_id = @userId AND provider_player_id = @puuid AND act_id = @actId
                      AND queue_id = 'competitive' AND match_data_version >= 1
                    """, new { userId, puuid = account.Puuid, actId = act.ActId });
                var versionRebuild = !actReset && existing is { AggregationVersion: < 4 } && coversActStart && archiveComplete
                    && archivedCount >= existing.MatchesAnalyzed && (existing.LastMatchId is null || MatchlistContains(listBody, existing.LastMatchId));
                if (versionRebuild)
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO public.valorant_aggregate_snapshots (user_id, provider_player_id, act_id, snapshot)
                        SELECT user_id, provider_player_id, last_act_id, to_jsonb(pgs)
                        FROM public.player_game_stats pgs
                        WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
                        ON CONFLICT (user_id, provider_player_id, act_id) DO NOTHING
                        """, new { userId, puuid = account.Puuid });
                    actReset = true;
                }
                var version = actReset || existing is null || existing.MatchesAnalyzed == 0 ? 4 : existing.AggregationVersion;
                var lastMatchId = actReset ? null : existing?.LastMatchId;
                var cursorGap = lastMatchId is not null && !MatchlistContains(listBody, lastMatchId);
                List<string> matchIds;
                if (cursorGap)
                {
                    var cursorTime = await conn.QuerySingleOrDefaultAsync<DateTime?>("""
                        SELECT started_at FROM public.valorant_player_matches
                        WHERE user_id = @userId AND provider_player_id = @puuid
                          AND match_id = @lastMatchId AND act_id = @actId
                        """, new { userId, puuid = account.Puuid, lastMatchId, actId = act.ActId });
                    if (cursorTime is null)
                    {
                        logger.LogWarning("ValorantStatsSync: missing cursor for user {UserId}; preserving previous totals", userId);
                        await conn.ExecuteAsync("UPDATE public.player_game_stats SET sync_status = 'partial', sync_error = @error, updated_at = now() WHERE user_id = @userId AND game_slug = 'valorant'",
                            new { userId, error = "Match history is incomplete; previous stats were preserved." });
                        return;
                    }
                    matchIds = (await conn.QueryAsync<string>("""
                        SELECT match_id FROM public.valorant_player_matches
                        WHERE user_id = @userId AND provider_player_id = @puuid AND act_id = @actId
                          AND queue_id = 'competitive' AND started_at > @cursorTime
                        ORDER BY started_at DESC, match_id DESC
                        """, new { userId, puuid = account.Puuid, actId = act.ActId, cursorTime })).ToList();
                }
                else
                    matchIds = ParseCompetitiveMatchIds(listBody, lastMatchId, actReset, act.StartTime);
                if (matchIds.Count == 0)
                {
                    await conn.ExecuteAsync(
                        """
                    UPDATE public.player_game_stats SET
                        competitive_tier = CASE WHEN @reset THEN NULL ELSE competitive_tier END,
                        competitive_tier_name = CASE WHEN @reset THEN NULL ELSE competitive_tier_name END,
                        sync_error = NULL,
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
                        total_headshots = CASE WHEN @reset THEN 0 ELSE total_headshots END,
                        total_shots = CASE WHEN @reset THEN 0 ELSE total_shots END,
                        headshot_data_complete = CASE WHEN @reset THEN false ELSE headshot_data_complete END,
                        hs_pct = CASE WHEN @reset THEN NULL ELSE hs_pct END,
                        player_card_id = COALESCE((SELECT vm.player_card_id FROM public.valorant_player_matches vm
                            WHERE vm.user_id = @userId AND vm.provider_player_id = @puuid AND vm.act_id = @actId
                              AND vm.player_card_id IS NOT NULL
                            ORDER BY vm.started_at DESC LIMIT 1), player_card_id),
                        last_match_id = CASE WHEN @reset THEN NULL ELSE last_match_id END,
                        aggregation_version = @version, sync_status = @status, synced_at = now(), updated_at = now(), last_act_id = @actId
                    WHERE user_id = @userId AND game_slug = 'valorant'
                    """,
                        new
                        {
                            userId,
                            puuid = account.Puuid,
                            actId = act.ActId,
                            reset = actReset,
                            version,
                            status = coversActStart && archiveComplete && !cursorGap ? "complete" : "partial"
                        });
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
                        TotalHeadshots = existing?.TotalHeadshots ?? 0,
                        TotalShots = existing?.TotalShots ?? 0,
                        // A verified hit-data repair remains valid on legacy aggregate versions.
                        HeadshotDataComplete = existing is null || existing.MatchesAnalyzed == 0 || existing.HeadshotDataComplete,
                    };

                if (!actReset && existing is not null) RestoreAgentStats(existing.PopularAgents, stats.AgentStats);

                var archived = (await conn.QueryAsync<ValorantMatchArchive.Match>("""
                    SELECT match_id AS id, act_id, started_at, queue_id, agent_id, won,
                           kills, deaths, assists, score, competitive_tier AS tier,
                           headshots, shots, headshot_data_complete, player_card_id, match_data_version
                    FROM public.valorant_player_matches
                    WHERE user_id = @userId AND provider_player_id = @puuid AND match_id = ANY(@ids)
                    """, new { userId, puuid = account.Puuid, ids = matchIds.ToArray() }))
                    .ToDictionary(match => match.Id, StringComparer.OrdinalIgnoreCase);

                string? newestMatchId = null;
                var rateLimited = false;

                foreach (var matchId in matchIds.AsEnumerable().Reverse())
                {
                    ct.ThrowIfCancellationRequested();

                    if (archived.TryGetValue(matchId, out var stored) &&
                        string.Equals(stored.QueueId, "competitive", StringComparison.OrdinalIgnoreCase) &&
                        stored.MatchDataVersion >= 1)
                    {
                        AccumulatePlayer(stats, stored.AgentId, stored.Kills, stored.Deaths, stored.Assists,
                            stored.Score, stored.Tier ?? 0, stored.Won, stored.Headshots, stored.Shots,
                            stored.PlayerCardId, stored.HeadshotDataComplete == true);
                        newestMatchId = matchId;
                        continue;
                    }

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

                if ((actReset && rateLimited && newestMatchId is null) || (versionRebuild && rateLimited))
                {
                    await MarkError(conn, userId, "Match details are temporarily unavailable; previous stats were preserved.");
                    return;
                }
                var agentMap = await GetAgentMapAsync(ct);
                var popularAgents = BuildPopularAgentsJson(stats.AgentStats, agentMap);
                var winRate = stats.TotalMatches > 0 ? Math.Round(100.0m * stats.Wins / stats.TotalMatches, 2) : (decimal?)null;
                var kdRatio = stats.Deaths > 0 ? Math.Round((decimal)stats.Kills / stats.Deaths, 2) : (decimal?)null;
                var avgScore = stats.TotalMatches > 0 ? stats.TotalScore / stats.TotalMatches : (int?)null;
                var hsPct = stats.HeadshotDataComplete && stats.TotalShots > 0 ? Math.Round(100.0m * stats.TotalHeadshots / stats.TotalShots, 2) : (decimal?)null;

                var tierName = stats.LatestCompetitiveTier > 0
                    ? await ResolveTierName(conn, stats.LatestCompetitiveTier)
                    : null;

                await conn.ExecuteAsync(
                    """
                UPDATE public.player_game_stats SET
                    competitive_tier      = CASE WHEN @reset THEN @tier ELSE COALESCE(@tier, competitive_tier) END,
                    competitive_tier_name = CASE WHEN @reset THEN @tierName ELSE COALESCE(@tierName, competitive_tier_name) END,
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
                    total_headshots       = @totalHeadshots,
                    total_shots           = @totalShots,
                    headshot_data_complete = @headshotDataComplete,
                    hs_pct                = @hsPct,
                    player_card_id        = CASE WHEN @playerCard IS NOT NULL THEN @playerCard ELSE player_card_id END,
                    last_match_id         = CASE WHEN @reset THEN @lastMatchId ELSE COALESCE(@lastMatchId, last_match_id) END,
                    last_act_id           = @actId,
                    aggregation_version   = @version,
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
                        totalHeadshots = stats.TotalHeadshots,
                        totalShots = stats.TotalShots,
                        headshotDataComplete = stats.HeadshotDataComplete,
                        hsPct,
                        playerCard = archivePlayerCard ?? stats.LatestPlayerCard,
                        lastMatchId = newestMatchId,
                        reset = actReset,
                        version,
                        actId = act.ActId,
                        status = rateLimited || !coversActStart || !archiveComplete || cursorGap ? "partial" : "complete",
                    });

                logger.LogInformation(
                    "ValorantStatsSync: synced {New} new matches (act total {Total}) for user {UserId} — tier {Tier}, {WinRate}% WR, {HsPct}% HS, act {ActId}",
                    matchIds.Count, stats.TotalMatches, userId, stats.LatestCompetitiveTier, winRate, hsPct, act.ActId);
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

    private sealed record RefreshState(DateTime? SyncedAt, DateTime? RequestedAt, string? Status, bool SameAccount);

    private static async Task<DateTime?> BeginRefreshAsync(System.Data.IDbConnection conn, Guid userId, string puuid, CancellationToken ct)
    {
        var state = await conn.QuerySingleOrDefaultAsync<RefreshState>(new CommandDefinition("""
            SELECT CASE WHEN last_act_id IS NOT NULL THEN synced_at END AS syncedat,
                   sync_requested_at AS requestedat, sync_status AS status,
                   provider_player_id = @puuid AS sameaccount
            FROM public.player_game_stats WHERE user_id = @userId AND game_slug = 'valorant'
            """, new { userId, puuid }, cancellationToken: ct));
        if (state?.SameAccount == true && (ValorantStatsFreshness.Remaining(state.SyncedAt) > 0 ||
            (state.Status != "pending" && ValorantStatsFreshness.Remaining(state.RequestedAt) > 0)))
        {
            await ValorantHeadshotRecovery.RepairSavedAsync(conn, userId, ct);
            // A duplicate queued job must not call Riot or leave a fresh snapshot stuck as pending.
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE public.player_game_stats SET sync_status = 'partial', updated_at = now()
                WHERE user_id = @userId AND game_slug = 'valorant' AND provider_player_id = @puuid
                  AND sync_status IN ('pending', 'syncing')
                """, new { userId, puuid }, cancellationToken: ct));
            return null;
        }
        return state is { Status: "pending", RequestedAt: not null } ? state.RequestedAt : DateTime.UtcNow;
    }

    private static bool MatchlistContains(string json, string matchId)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array &&
            history.EnumerateArray().Any(entry => entry.TryGetProperty("matchId", out var id) && id.GetString() == matchId);
    }

    private static bool HistoryCoversActStart(string json, DateTimeOffset actStart)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array &&
            history.EnumerateArray().Any(entry => entry.TryGetProperty("gameStartTimeMillis", out var time) &&
                time.TryGetInt64(out var value) && value < actStart.ToUnixTimeMilliseconds());
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
                Deaths = item.GetProperty("deaths").GetInt32(),
                Assists = item.TryGetProperty("assists", out var a) && a.TryGetInt32(out var av) ? av : 0,
                Headshots = item.TryGetProperty("headshots", out var hs) && hs.TryGetInt32(out var hsv) ? hsv : 0,
                Shots = item.TryGetProperty("shots", out var sh) && sh.TryGetInt32(out var shv) ? shv : 0,
                HeadshotDataComplete = item.TryGetProperty("headshot_data_complete", out var complete) && complete.ValueKind == JsonValueKind.True,
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = history.EnumerateArray()
            .Where(entry => entry.TryGetProperty("gameStartTimeMillis", out var time) &&
                            time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out _))
            .OrderByDescending(entry => entry.GetProperty("gameStartTimeMillis").GetInt64());
        foreach (var entry in entries)
        {
            var matchId = entry.TryGetProperty("matchId", out var mid) ? mid.GetString() : null;
            if (string.IsNullOrWhiteSpace(matchId) || !seen.Add(matchId)) continue;
            if (!actReset && matchId == lastKnownMatchId) break;
            var startMs = entry.GetProperty("gameStartTimeMillis").GetInt64();
            if (startMs < actStartMs) break;
            var queueId = entry.TryGetProperty("queueId", out var q) ? q.GetString() : null;
            if (string.Equals(queueId, "competitive", StringComparison.OrdinalIgnoreCase)) ids.Add(matchId);
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
            var playerCard = player.TryGetProperty("playerCard", out var pc) ? pc.GetString() : null;
            var tier = player.TryGetProperty("competitiveTier", out var ct) && ct.TryGetInt32(out var tierVal)
                ? tierVal : 0;

            var (headshots, shots, headshotDataComplete) = ValorantMatchArchive.ExtractHeadshotData(root, puuid);

            var hasWinner = teamWins.Values.Any(won => won);
            bool? result = hasWinner && teamWins.TryGetValue(playerTeamId, out var won) ? won : null;
            AccumulatePlayer(stats, characterId, kills, deaths, assists, score, tier, result,
                headshots, shots, playerCard, headshotDataComplete);

            return true;
        }
        return false;
    }

    private static void AccumulatePlayer(AggregatedStats stats, string? characterId, int kills,
        int deaths, int assists, int score, int tier, bool? won,
        int headshots, int shots, string? playerCard, bool headshotDataComplete)
    {
        stats.TotalMatches++;
        stats.Kills += kills;
        stats.Deaths += deaths;
        stats.Assists += assists;
        stats.TotalScore += score;
        stats.TotalHeadshots += headshots;
        stats.TotalShots += shots;
        stats.HeadshotDataComplete &= headshotDataComplete;
        if (won == true) stats.Wins++; else if (won == false) stats.Losses++;
        if (tier >= 3) stats.LatestCompetitiveTier = tier;
        if (!string.IsNullOrEmpty(playerCard)) stats.LatestPlayerCard = playerCard;
        if (string.IsNullOrEmpty(characterId)) return;
        if (!stats.AgentStats.TryGetValue(characterId, out var agentStat))
        {
            agentStat = new AgentStat { CharacterId = characterId };
            stats.AgentStats[characterId] = agentStat;
        }
        agentStat.GamesPlayed++;
        agentStat.Kills += kills;
        agentStat.Deaths += deaths;
        agentStat.Assists += assists;
        agentStat.Headshots += headshots;
        agentStat.Shots += shots;
        agentStat.HeadshotDataComplete &= headshotDataComplete;
        if (won == true) agentStat.Wins++;
    }

    private static async Task<string?> ResolveTierName(System.Data.IDbConnection conn, int competitiveTier)
    {
        var sortOrder = competitiveTier - 3;
        return await conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT r.display_name FROM public.game_catalog_rank_tiers r
            JOIN public.game_catalog_versions v ON v.id = r.version_id AND v.is_active
            WHERE r.game_slug = 'valorant' AND r.sort_order = @sortOrder
            LIMIT 1
            """,
            new { sortOrder });
    }

    private async Task<ActInfo> GetCurrentActAsync(CancellationToken ct)
    {
        try
        {
            return await cache.GetOrCreateAsync(
                "valorant:current-act:v2",
                async _ =>
                {
                    var client = httpFactory.CreateClient();
                    using var resp = await client.GetAsync(ValorantSeasonsUrl, ct);
                    resp.EnsureSuccessStatusCode();
                    var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Season data is unavailable.");
                    return FindCurrentAct(data, DateTimeOffset.UtcNow)
                        ?? throw new InvalidOperationException("No active Valorant act found.");
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) },
                cancellationToken: ct) ?? new ActInfo("unknown", DateTimeOffset.UtcNow.AddMonths(-2));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ValorantStatsSync: could not fetch season data");
            return new ActInfo("unknown", DateTimeOffset.UtcNow.AddMonths(-2));
        }
    }

    private static ActInfo? FindCurrentAct(JsonElement data, DateTimeOffset now)
    {
        var acts = new List<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var season in data.EnumerateArray())
        {
            var type = season.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is null || !type.EndsWith("::Act", StringComparison.OrdinalIgnoreCase)) continue;
            var uuid = season.TryGetProperty("uuid", out var u) ? u.GetString() : null;
            var start = season.TryGetProperty("startTime", out var st) ? st.GetString() : null;
            var end = season.TryGetProperty("endTime", out var et) ? et.GetString() : null;
            if (uuid is not null && DateTimeOffset.TryParse(start, out var actStart) &&
                DateTimeOffset.TryParse(end, out var actEnd) && actStart < actEnd)
                acts.Add((uuid, actStart, actEnd));
        }
        var current = acts.Where(a => a.Start <= now && now < a.End).OrderByDescending(a => a.Start).FirstOrDefault();
        if (current.Id is null) return null;
        return new ActInfo(current.Id, current.Start);
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
                var agentHsPct = a.HeadshotDataComplete && a.Shots > 0 ? Math.Round(100.0m * a.Headshots / a.Shots, 2) : (decimal?)null;
                return new
                {
                    agent_id = a.CharacterId,
                    display_name = name,
                    icon_url = iconUrl,
                    games_played = a.GamesPlayed,
                    wins = a.Wins,
                    kills = a.Kills,
                    deaths = a.Deaths,
                    assists = a.Assists,
                    headshots = a.Headshots,
                    shots = a.Shots,
                    headshot_data_complete = a.HeadshotDataComplete,
                    kd_ratio = agentKd,
                    hs_pct = agentHsPct,
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
        int MatchesAnalyzed, int TotalScore, string? PopularAgents, int AggregationVersion, string? SyncStatus,
        int TotalHeadshots, int TotalShots, bool HeadshotDataComplete);

    private sealed class AggregatedStats
    {
        public int TotalMatches;
        public int Wins;
        public int Losses;
        public int Kills;
        public int Deaths;
        public int Assists;
        public int TotalScore;
        public int TotalHeadshots;
        public int TotalShots;
        public bool HeadshotDataComplete = true;
        public int LatestCompetitiveTier;
        public string? LatestPlayerCard;
        public Dictionary<string, AgentStat> AgentStats = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AgentStat
    {
        public required string CharacterId { get; init; }
        public int GamesPlayed;
        public int Wins;
        public int Kills;
        public int Deaths;
        public int Assists;
        public int Headshots;
        public int Shots;
        public bool HeadshotDataComplete = true;
    }
}
