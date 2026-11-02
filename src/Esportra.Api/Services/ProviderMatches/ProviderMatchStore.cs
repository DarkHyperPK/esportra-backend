using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;

namespace Esportra.Api.Services.ProviderMatches;

/// <summary>Writes normalized match rows and applies a verification result. Every method runs inside the caller's transaction.</summary>
public static class ProviderMatchStore
{
    public static string Sha256(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    /// <summary>Serializes ingests of one Riot match: concurrent jobs queue up instead of interleaving deletes and inserts.</summary>
    public static Task LockMatchAsync(IDbConnection conn, IDbTransaction tx, string providerMatchId) =>
        conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@id, 0))", new { id = "provider-match:" + providerMatchId }, tx);

    public static Task SavePayloadAsync(IDbConnection conn, IDbTransaction tx, Guid matchId, string payload) =>
        conn.ExecuteAsync(
            """
            INSERT INTO public.provider_match_payloads (match_id, payload, payload_sha256)
            VALUES (@matchId, @payload::jsonb, @sha)
            ON CONFLICT (match_id) DO UPDATE SET payload = EXCLUDED.payload, payload_sha256 = EXCLUDED.payload_sha256, fetched_at = now()
            """, new { matchId, payload, sha = Sha256(payload) }, tx);

    public static Task SetStatusAsync(IDbConnection conn, IDbTransaction? tx, Guid matchId, string status, string? detail, bool countAttempt) =>
        conn.ExecuteAsync(
            """
            UPDATE public.provider_matches
            SET data_status = @status, status_detail = @detail, updated_at = now(),
                fetch_attempts = fetch_attempts + @attempt
            WHERE id = @matchId
            """, new { matchId, status, detail, attempt = countAttempt ? 1 : 0 }, tx);

    /// <summary>Replaces every derived row of a match. Re-running with the same payload leaves identical rows.</summary>
    public static async Task ReplaceNormalizedAsync(
        IDbConnection conn, IDbTransaction tx, Guid matchId, string shard, NormalizedValorantMatch m)
    {
        var i = m.Info;
        await conn.ExecuteAsync(
            """
            UPDATE public.provider_matches
            SET map_provider_id = @MapId, queue_id = @QueueId, is_custom = @IsCustom, game_mode = @GameMode,
                started_at = @StartedAt, length_ms = @LengthMs, shard = @shard, game_version = @GameVersion,
                season_id = @SeasonId, is_completed = @IsCompleted, parser_version = @ver,
                data_status = CASE WHEN @IsCompleted THEN 'retrieved' ELSE 'incomplete' END,
                status_detail = NULL, ingested_at = now(), updated_at = now()
            WHERE id = @matchId
            """,
            new { i.MapId, i.QueueId, i.IsCustom, i.GameMode, i.StartedAt, i.LengthMs, shard, i.GameVersion, i.SeasonId, i.IsCompleted, ver = ValorantMatchNormalizer.ParserVersion, matchId }, tx);

        foreach (var table in new[]
                 {
                     "valorant_match_kills", "valorant_round_damage", "valorant_round_player_stats", "valorant_match_rounds",
                     "valorant_match_players", "valorant_match_teams",
                 })
            await conn.ExecuteAsync($"DELETE FROM public.{table} WHERE match_id = @matchId", new { matchId }, tx);

        await conn.ExecuteAsync(
            "INSERT INTO public.valorant_match_teams (match_id, team_id, won, rounds_played, rounds_won, num_points) VALUES (@matchId, @TeamId, @Won, @RoundsPlayed, @RoundsWon, @NumPoints)",
            m.Teams.Select(t => new { matchId, t.TeamId, t.Won, t.RoundsPlayed, t.RoundsWon, t.NumPoints }), tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.valorant_match_players
                (match_id, puuid, riot_side, game_name, tag_line, party_id, character_id, competitive_tier, is_observer,
                 score, rounds_played, kills, deaths, assists, playtime_ms, grenade_casts, ability1_casts, ability2_casts,
                 ultimate_casts, damage_dealt, headshots, bodyshots, legshots, first_kills, first_deaths,
                 kills_2k, kills_3k, kills_4k, kills_5k, plants, defuses)
            VALUES (@matchId, @Puuid, @RiotSide, @GameName, @TagLine, @PartyId, @CharacterId, @CompetitiveTier, @IsObserver,
                    @Score, @RoundsPlayed, @Kills, @Deaths, @Assists, @PlaytimeMs, @GrenadeCasts, @Ability1Casts, @Ability2Casts,
                    @UltimateCasts, @DamageDealt, @Headshots, @Bodyshots, @Legshots, @FirstKills, @FirstDeaths,
                    @Kills2K, @Kills3K, @Kills4K, @Kills5K, @Plants, @Defuses)
            """,
            m.Players.Select(p => new
            {
                matchId,
                p.Puuid,
                p.RiotSide,
                p.GameName,
                p.TagLine,
                p.PartyId,
                p.CharacterId,
                p.CompetitiveTier,
                p.IsObserver,
                p.Score,
                p.RoundsPlayed,
                p.Kills,
                p.Deaths,
                p.Assists,
                p.PlaytimeMs,
                p.GrenadeCasts,
                p.Ability1Casts,
                p.Ability2Casts,
                p.UltimateCasts,
                p.DamageDealt,
                p.Headshots,
                p.Bodyshots,
                p.Legshots,
                p.FirstKills,
                p.FirstDeaths,
                p.Kills2K,
                p.Kills3K,
                p.Kills4K,
                p.Kills5K,
                p.Plants,
                p.Defuses,
            }), tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.valorant_match_rounds
                (match_id, round_num, round_result, round_result_code, round_ceremony, winning_team, winning_team_role,
                 bomb_planter, bomb_defuser, plant_round_time_ms, plant_site, plant_x, plant_y, defuse_round_time_ms, defuse_x, defuse_y)
            VALUES (@matchId, @RoundNum, @RoundResult, @RoundResultCode, @RoundCeremony, @WinningTeam, @WinningTeamRole,
                    @BombPlanter, @BombDefuser, @PlantRoundTimeMs, @PlantSite, @PlantX, @PlantY, @DefuseRoundTimeMs, @DefuseX, @DefuseY)
            """,
            m.Rounds.Select(r => new
            {
                matchId,
                r.RoundNum,
                r.RoundResult,
                r.RoundResultCode,
                r.RoundCeremony,
                r.WinningTeam,
                r.WinningTeamRole,
                r.BombPlanter,
                r.BombDefuser,
                r.PlantRoundTimeMs,
                r.PlantSite,
                r.PlantX,
                r.PlantY,
                r.DefuseRoundTimeMs,
                r.DefuseX,
                r.DefuseY,
            }), tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.valorant_round_player_stats
                (match_id, round_num, puuid, kills, score, loadout_value, weapon, armor, spent, remaining, ability)
            VALUES (@matchId, @RoundNum, @Puuid, @Kills, @Score, @LoadoutValue, @Weapon, @Armor, @Spent, @Remaining, @AbilityJson::jsonb)
            """,
            m.RoundPlayerStats.Select(s => new { matchId, s.RoundNum, s.Puuid, s.Kills, s.Score, s.LoadoutValue, s.Weapon, s.Armor, s.Spent, s.Remaining, s.AbilityJson }), tx);

        await conn.ExecuteAsync(
            "INSERT INTO public.valorant_round_damage (match_id, round_num, puuid, receiver, damage, headshots, bodyshots, legshots) VALUES (@matchId, @RoundNum, @Puuid, @Receiver, @Damage, @Headshots, @Bodyshots, @Legshots)",
            m.Damage.Select(d => new { matchId, d.RoundNum, d.Puuid, d.Receiver, d.Damage, d.Headshots, d.Bodyshots, d.Legshots }), tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.valorant_match_kills
                (match_id, round_num, kill_ordinal, killer, victim, assistants, time_since_game_ms, time_since_round_ms,
                 victim_x, victim_y, damage_type, damage_item, is_secondary_fire, player_locations)
            VALUES (@matchId, @RoundNum, @Ordinal, @Killer, @Victim, @Assistants, @TimeSinceGameMs, @TimeSinceRoundMs,
                    @VictimX, @VictimY, @DamageType, @DamageItem, @IsSecondaryFire, @PlayerLocationsJson::jsonb)
            """,
            m.Kills.Select(k => new { matchId, k.RoundNum, k.Ordinal, k.Killer, k.Victim, k.Assistants, k.TimeSinceGameMs, k.TimeSinceRoundMs, k.VictimX, k.VictimY, k.DamageType, k.DamageItem, k.IsSecondaryFire, k.PlayerLocationsJson }), tx);
    }

    /// <summary>Stores the verification result and resolves the frozen roster against the match (only played / side / name change).</summary>
    public static async Task ApplyVerificationAsync(
        IDbConnection conn, IDbTransaction tx, Guid associationId, NormalizedValorantMatch match, VerificationResult result)
    {
        var before = await conn.QuerySingleAsync<(string Status, string Detail)>(
            "SELECT verification_status, verification_detail::text FROM public.provider_match_associations WHERE id = @associationId FOR UPDATE",
            new { associationId }, tx);
        var detail = JsonSerializer.Serialize(new { reasons = result.Reasons });
        var verifiedAt = result.Status is ProviderMatchVerifier.Pending ? (DateTime?)null : DateTime.UtcNow;

        await conn.ExecuteAsync(
            """
            UPDATE public.provider_match_associations
            SET team1_riot_side = @Team1Side, team2_riot_side = @Team2Side, verification_status = @Status,
                verification_detail = @detail::jsonb, verified_at = @verifiedAt, updated_at = now()
            WHERE id = @associationId
            """, new { result.Team1Side, result.Team2Side, result.Status, detail, verifiedAt, associationId }, tx);

        var names = match.Players.ToDictionary(p => p.Puuid, p => $"{p.GameName}#{p.TagLine}");
        var playing = result.Players.Select(p => p.Puuid).ToHashSet();
        await conn.ExecuteAsync(
            """
            UPDATE public.provider_match_roster_snapshots s
            SET played = TRUE, riot_side = x.riot_side, riot_name = x.riot_name
            FROM (
                SELECT p AS puuid,
                       (@sides::jsonb ->> p) AS riot_side,
                       (@names::jsonb ->> p) AS riot_name
                FROM unnest(@all) AS p
            ) x
            WHERE s.association_id = @associationId AND s.puuid = x.puuid
            """,
            new
            {
                all = playing.ToArray(),
                associationId,
                sides = JsonSerializer.Serialize(result.Players.ToDictionary(p => p.Puuid, p => p.RiotSide)),
                names = JsonSerializer.Serialize(names),
            }, tx);
        // Linked roster members who did not appear in the match did not play it.
        await conn.ExecuteAsync(
            "UPDATE public.provider_match_roster_snapshots SET played = FALSE, riot_side = NULL, riot_name = NULL WHERE association_id = @associationId AND puuid IS NOT NULL AND NOT (puuid = ANY(@playing))",
            new { associationId, playing = playing.ToArray() }, tx);
        await conn.ExecuteAsync(
            "DELETE FROM public.provider_match_roster_snapshots WHERE association_id = @associationId AND identity_status = 'unmatched' AND NOT (puuid = ANY(@keep))",
            new { associationId, keep = result.Players.Where(p => p.Slot is null).Select(p => p.Puuid).ToArray() }, tx);
        // Riot players who are not roster members stay visible as unmatched (identity unknown, never invented).
        foreach (var p in result.Players.Where(p => p.Slot is null))
            await conn.ExecuteAsync(
                """
                INSERT INTO public.provider_match_roster_snapshots (association_id, puuid, identity_status, played, riot_side, riot_name)
                VALUES (@associationId, @Puuid, 'unmatched', TRUE, @RiotSide, @name)
                ON CONFLICT (association_id, puuid) WHERE user_id IS NULL AND puuid IS NOT NULL
                DO UPDATE SET played = TRUE, riot_side = EXCLUDED.riot_side, riot_name = EXCLUDED.riot_name
                """, new { associationId, p.Puuid, p.RiotSide, name = names.GetValueOrDefault(p.Puuid) }, tx);

        if (before.Status != result.Status || !ReasonsOf(before.Detail).SequenceEqual(result.Reasons))
            await ProviderMatchRegistry.AddEventAsync(conn, tx, associationId, "verification", null,
                new { status = before.Status }, new { status = result.Status, reasons = result.Reasons });
    }

    private static IReadOnlyList<string> ReasonsOf(string detailJson)
    {
        using var doc = JsonDocument.Parse(detailJson);
        return doc.RootElement.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
    }
}
