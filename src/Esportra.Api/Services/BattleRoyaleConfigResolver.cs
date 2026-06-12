using System.Text.Json;
using Esportra.Core.Br;

namespace Esportra.Api.Services;

/// <summary>
/// Resolves Battle Royale config from tournament settings and per-stage config.br overrides.
/// Mirrors frontend <c>src/utils/brConfigResolve.ts</c>.
/// </summary>
public static class BattleRoyaleConfigResolver
{
    public sealed record BrScoringSettings(int[] Placements, int KillPoints, int? KillCap);

    public enum BrTiebreaker
    {
        MostWins,
        MostKills,
        HeadToHead,
    }

    public enum BrMapMode
    {
        None,
        FixedStage,
        PerRound,
        Rotation,
    }

    public enum BrStageFormat
    {
        SingleLobby,
        StaticGroups,
        GroupRotation,
        MultiLobbyCut,
    }

    public enum BrLeaderboardScope
    {
        StageGlobal,
        PerSeedGroup,
        PerLobby,
    }

    public enum BrAdvancementMode
    {
        TopNPerGroup,
        TopNPerLobby,
        TopNOverall,
        Threshold,
        None,
    }

    public enum BrLobbyFormation
    {
        Single,
        PerSeedGroup,
        WavePairings,
        ParallelCut,
    }

    public sealed record BrMapConfig(BrMapMode Mode, IReadOnlyList<string> Pool, string? FixedMap);

    public sealed record ResolvedStageBrConfig(
        BrScoringSettings Scoring,
        BrTiebreaker Tiebreaker,
        BrMapConfig Map,
        int? GameCount);

    public sealed record BrLeaderboardAggregate(
        long TotalPoints,
        long Wins,
        long TotalKills,
        double AvgPlacement);

    public static ResolvedStageBrConfig Resolve(
        object? tournamentSettings,
        object? stageConfig,
        object? catalogBrConfig,
        int? stageCapacity = null)
    {
        return new ResolvedStageBrConfig(
            ResolveScoring(tournamentSettings, stageConfig, catalogBrConfig),
            ResolveTiebreaker(tournamentSettings),
            ResolveMapConfig(tournamentSettings, stageConfig, catalogBrConfig),
            ResolveGameCount(tournamentSettings, stageConfig));
    }

    public static BrScoringSettings ResolveScoring(
        object? tournamentSettings,
        object? stageConfig,
        object? catalogBrConfig)
    {
        // Scoring is tournament-wide only — stage config.br.scoring overrides are ignored.
        var fallbackPresetKey = BrCatalogBrConfigHelper.ReadDefaultPreset(catalogBrConfig);
        var fallback = ResolvePresetScoring(fallbackPresetKey);
        return ResolveTournamentScoring(tournamentSettings, fallbackPresetKey, fallback, catalogBrConfig);
    }

    public static BrTiebreaker ResolveTiebreaker(object? tournamentSettings)
    {
        if (!TryParseJsonElement(tournamentSettings, out var root))
            return BrTiebreaker.MostWins;

        var settingsRoot = ResolveBrSettingsRoot(root);
        if (TryGetPropertyIgnoreCase(settingsRoot, "brTiebreaker", out var tiebreakerEl)
            && tiebreakerEl.ValueKind == JsonValueKind.String)
        {
            return tiebreakerEl.GetString()?.Trim().ToLowerInvariant() switch
            {
                "most_kills" => BrTiebreaker.MostKills,
                "head_to_head" => BrTiebreaker.HeadToHead,
                _ => BrTiebreaker.MostWins,
            };
        }

        return BrTiebreaker.MostWins;
    }

    public static BrMapConfig ResolveMapConfig(
        object? tournamentSettings,
        object? stageConfig,
        object? catalogBrConfig)
    {
        var catalogPool = BrCatalogBrConfigHelper.ReadMapPool(catalogBrConfig);
        var hasMaps = BrCatalogBrConfigHelper.ReadHasMaps(catalogBrConfig, catalogPool);

        var defaultMode = BrMapMode.None;
        if (hasMaps)
        {
            defaultMode = BrCatalogBrConfigHelper.ReadDefaultMapMode(catalogBrConfig) ?? BrMapMode.PerRound;
            if (TryParseJsonElement(tournamentSettings, out var settingsRoot))
            {
                var brSettings = ResolveBrSettingsRoot(settingsRoot);
                if (TryGetPropertyIgnoreCase(brSettings, "brDefaultMapMode", out var modeEl)
                    && modeEl.ValueKind == JsonValueKind.String)
                {
                    defaultMode = ParseMapMode(modeEl.GetString()) ?? defaultMode;
                }
            }
        }

        if (!TryParseJsonElement(stageConfig, out var stageRoot)
            || !TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            || brSection.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(brSection, "map", out var stageMap)
            || stageMap.ValueKind != JsonValueKind.Object)
        {
            return new BrMapConfig(hasMaps ? defaultMode : BrMapMode.None, catalogPool, catalogPool.FirstOrDefault());
        }

        var mode = hasMaps
            ? ParseMapMode(
                TryGetPropertyIgnoreCase(stageMap, "mode", out var stageModeEl) && stageModeEl.ValueKind == JsonValueKind.String
                    ? stageModeEl.GetString()
                    : null) ?? defaultMode
            : BrMapMode.None;

        var pool = ReadStringArray(stageMap, "pool");
        if (pool.Count == 0)
            pool = catalogPool.ToList();

        string? fixedMap = null;
        if (TryGetPropertyIgnoreCase(stageMap, "fixedMap", out var fixedMapEl)
            && fixedMapEl.ValueKind == JsonValueKind.String)
        {
            fixedMap = fixedMapEl.GetString();
        }

        fixedMap ??= pool.FirstOrDefault();

        return new BrMapConfig(mode, pool, fixedMap);
    }

    /// <summary>Games played inside each physical lobby (Cash Cup model).</summary>
    public static int? ResolveGamesPerLobby(object? tournamentSettings, object? stageConfig) =>
        ResolveGameCount(tournamentSettings, stageConfig);

    public static int? ResolveGameCount(object? tournamentSettings, object? stageConfig)
    {
        if (TryParseJsonElement(stageConfig, out var stageRoot)
            && TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            && brSection.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(brSection, "gamesPerLobby", out var gamesPerLobbyEl)
                && gamesPerLobbyEl.TryGetInt32(out var gamesPerLobby)
                && gamesPerLobby > 0)
            {
                return gamesPerLobby;
            }

            if (TryGetPropertyIgnoreCase(brSection, "gameCount", out var gameCountEl)
                && gameCountEl.TryGetInt32(out var stageGameCount)
                && stageGameCount > 0)
            {
                return stageGameCount;
            }
        }

        if (TryParseJsonElement(tournamentSettings, out var settingsRoot))
        {
            var brSettings = ResolveBrSettingsRoot(settingsRoot);
            if (TryGetPropertyIgnoreCase(brSettings, "brDefaultGameCount", out var defaultGameCountEl)
                && defaultGameCountEl.TryGetInt32(out var defaultGameCount)
                && defaultGameCount > 0)
            {
                return defaultGameCount;
            }

            if (TryGetPropertyIgnoreCase(brSettings, "brGameCount", out var legacyGameCountEl)
                && legacyGameCountEl.TryGetInt32(out var legacyGameCount)
                && legacyGameCount > 0)
            {
                return legacyGameCount;
            }
        }

        return 6;
    }

    public static BrLobbyFormation ResolveLobbyFormation(object? stageConfig) =>
        ResolveLobbyFormation(ResolveFormat(stageConfig));

    public static BrLobbyFormation ResolveLobbyFormation(BrStageFormat format) =>
        format switch
        {
            BrStageFormat.SingleLobby => BrLobbyFormation.Single,
            BrStageFormat.StaticGroups => BrLobbyFormation.PerSeedGroup,
            BrStageFormat.GroupRotation => BrLobbyFormation.WavePairings,
            BrStageFormat.MultiLobbyCut => BrLobbyFormation.ParallelCut,
            _ => BrLobbyFormation.PerSeedGroup,
        };

    public static BrStageFormat ResolveFormat(object? stageConfig)
    {
        if (TryParseJsonElement(stageConfig, out var stageRoot)
            && TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            && brSection.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(brSection, "format", out var formatEl)
            && formatEl.ValueKind == JsonValueKind.String)
        {
            return formatEl.GetString()?.Trim().ToLowerInvariant() switch
            {
                "single_lobby" => BrStageFormat.SingleLobby,
                "group_rotation" => BrStageFormat.GroupRotation,
                "multi_lobby_cut" => BrStageFormat.MultiLobbyCut,
                _ => BrStageFormat.StaticGroups
            };
        }

        return BrStageFormat.StaticGroups;
    }

    public static BrLeaderboardScope ResolveLeaderboardScope(object? stageConfig)
    {
        if (TryParseJsonElement(stageConfig, out var stageRoot)
            && TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            && brSection.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(brSection, "leaderboardScope", out var scopeEl)
            && scopeEl.ValueKind == JsonValueKind.String)
        {
            return scopeEl.GetString()?.Trim().ToLowerInvariant() switch
            {
                "per_seed_group" => BrLeaderboardScope.PerSeedGroup,
                "per_lobby" => BrLeaderboardScope.PerLobby,
                _ => BrLeaderboardScope.StageGlobal
            };
        }

        return ResolveLeaderboardScopeForFormat(ResolveFormat(stageConfig));
    }

    public static BrLeaderboardScope ResolveLeaderboardScopeForFormat(BrStageFormat format) =>
        format switch
        {
            BrStageFormat.SingleLobby => BrLeaderboardScope.StageGlobal,
            BrStageFormat.GroupRotation => BrLeaderboardScope.StageGlobal,
            BrStageFormat.MultiLobbyCut => BrLeaderboardScope.PerLobby,
            _ => BrLeaderboardScope.PerSeedGroup,
        };

    public static BrAdvancementMode ResolveAdvancement(object? stageConfig)
    {
        if (TryParseJsonElement(stageConfig, out var stageRoot)
            && TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            && brSection.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(brSection, "advancement", out var advancementObj)
                && advancementObj.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(advancementObj, "mode", out var modeEl)
                && modeEl.ValueKind == JsonValueKind.String)
            {
                return ParseAdvancementMode(modeEl.GetString());
            }

            if (TryGetPropertyIgnoreCase(brSection, "advancementMode", out var legacyEl)
                && legacyEl.ValueKind == JsonValueKind.String)
            {
                return ParseAdvancementMode(legacyEl.GetString());
            }
        }

        return BrAdvancementMode.TopNPerGroup;
    }

    public static int? ResolveAdvancementCount(object? stageConfig, int? stageAdvancementCount)
    {
        if (TryParseJsonElement(stageConfig, out var stageRoot)
            && TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            && brSection.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(brSection, "advancement", out var advancementObj)
            && advancementObj.ValueKind == JsonValueKind.Object)
        {
            var mode = ResolveAdvancement(stageConfig);
            return mode switch
            {
                BrAdvancementMode.TopNPerLobby when TryReadPositiveInt(advancementObj, "perLobby", out var perLobby) => perLobby,
                BrAdvancementMode.TopNOverall when TryReadPositiveInt(advancementObj, "overall", out var overall) => overall,
                BrAdvancementMode.Threshold when TryReadPositiveInt(advancementObj, "threshold", out var threshold) => threshold,
                BrAdvancementMode.TopNPerGroup when TryReadPositiveInt(advancementObj, "perGroup", out var perGroup) => perGroup,
                _ => stageAdvancementCount,
            };
        }

        return stageAdvancementCount;
    }

    private static BrAdvancementMode ParseAdvancementMode(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "top_n_per_lobby" => BrAdvancementMode.TopNPerLobby,
            "top_n_overall" => BrAdvancementMode.TopNOverall,
            "threshold" => BrAdvancementMode.Threshold,
            "none" => BrAdvancementMode.None,
            _ => BrAdvancementMode.TopNPerGroup,
        };

    private static bool TryReadPositiveInt(JsonElement parent, string property, out int value)
    {
        value = 0;
        return TryGetPropertyIgnoreCase(parent, property, out var el)
               && el.TryGetInt32(out value)
               && value > 0;
    }

    public static (int PlacementPoints, int KillPoints, int TotalPoints) CalculatePoints(
        int placement,
        int kills,
        BrScoringSettings scoring)
    {
        var placementPoints = placement >= 1 && placement <= scoring.Placements.Length
            ? scoring.Placements[placement - 1]
            : 0;
        var effectiveKills = scoring.KillCap is > 0 ? Math.Min(kills, scoring.KillCap.Value) : kills;
        var killPoints = effectiveKills * scoring.KillPoints;
        return (placementPoints, killPoints, placementPoints + killPoints);
    }

    public static string? ResolveMapForRound(BrMapConfig mapConfig, int roundNumber, string? explicitMap)
    {
        if (mapConfig.Mode == BrMapMode.None)
            return null;

        if (!string.IsNullOrWhiteSpace(explicitMap))
            return explicitMap.Trim();

        return mapConfig.Mode switch
        {
            BrMapMode.FixedStage => mapConfig.FixedMap,
            BrMapMode.Rotation when mapConfig.Pool.Count > 0 =>
                mapConfig.Pool[(roundNumber - 1) % mapConfig.Pool.Count],
            BrMapMode.PerRound => null,
            _ => null,
        };
    }

    public static bool ValidateMapInPool(BrMapConfig mapConfig, string? map, out string? error)
    {
        error = null;
        if (mapConfig.Mode == BrMapMode.None)
            return true;

        if (string.IsNullOrWhiteSpace(map))
        {
            error = "Map is required for this stage.";
            return false;
        }

        if (mapConfig.Pool.Count == 0)
            return true;

        if (!mapConfig.Pool.Contains(map.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            error = $"Map '{map}' is not in the configured map pool.";
            return false;
        }

        return true;
    }

    public static int CompareLeaderboardEntries(
        BrLeaderboardAggregate a,
        BrLeaderboardAggregate b,
        BrTiebreaker tiebreaker)
    {
        var coreTiebreaker = tiebreaker switch
        {
            BrTiebreaker.MostKills => Core.Br.BrTiebreaker.MostKills,
            BrTiebreaker.HeadToHead => Core.Br.BrTiebreaker.HeadToHead,
            _ => Core.Br.BrTiebreaker.MostWins,
        };

        return BrLeaderboardRanking.Compare(
            new Core.Br.BrLeaderboardAggregate(a.TotalPoints, a.Wins, a.TotalKills, a.AvgPlacement),
            new Core.Br.BrLeaderboardAggregate(b.TotalPoints, b.Wins, b.TotalKills, b.AvgPlacement),
            coreTiebreaker);
    }

    private static BrScoringSettings ResolveTournamentScoring(
        object? tournamentSettings,
        string? fallbackPresetKey,
        BrScoringSettings fallback,
        object? catalogBrConfig)
    {
        try
        {
            if (!TryParseJsonElement(tournamentSettings, out var root))
                return fallback;

            var settingsRoot = ResolveBrSettingsRoot(root);

            if (TryGetPropertyIgnoreCase(settingsRoot, "brCustomScoring", out var customScoring)
                && customScoring.ValueKind == JsonValueKind.Object)
            {
                var customPlacements = TryReadPlacementArray(customScoring);
                if (customPlacements.Length > 0
                    && TryGetPropertyIgnoreCase(customScoring, "killPoints", out var customKillPointsEl)
                    && customKillPointsEl.TryGetInt32(out var customKillPoints)
                    && customKillPoints >= 0)
                {
                    int? customKillCap = null;
                    if (TryGetPropertyIgnoreCase(customScoring, "killCap", out var customKillCapEl)
                        && customKillCapEl.ValueKind != JsonValueKind.Null
                        && customKillCapEl.TryGetInt32(out var parsedCustomKillCap)
                        && parsedCustomKillCap > 0)
                    {
                        customKillCap = parsedCustomKillCap;
                    }

                    return new BrScoringSettings(customPlacements, customKillPoints, customKillCap);
                }
            }

            string? presetKey = null;
            if (TryGetPropertyIgnoreCase(settingsRoot, "brScoringPreset", out var presetEl)
                && presetEl.ValueKind == JsonValueKind.String)
            {
                presetKey = presetEl.GetString();
            }

            var resolved = ResolvePresetScoring(presetKey ?? fallbackPresetKey);
            if (TryGetPropertyIgnoreCase(settingsRoot, "brKillCap", out var killCapEl)
                && killCapEl.ValueKind != JsonValueKind.Null
                && killCapEl.TryGetInt32(out var killCap)
                && killCap > 0)
            {
                resolved = resolved with { KillCap = killCap };
            }

            return resolved;
        }
        catch
        {
            return fallback;
        }
    }

    private static BrScoringSettings ResolvePresetScoring(string? presetKey)
    {
        return presetKey?.Trim().ToLowerInvariant() switch
        {
            "fncs" => new BrScoringSettings([25, 22, 20, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1], 1, null),
            "algs" => new BrScoringSettings([12, 9, 7, 5, 4, 3, 3, 2, 2, 2, 1, 1, 1, 1, 1], 1, 6),
            "pcs" => new BrScoringSettings([10, 6, 5, 4, 3, 2, 1, 1], 1, null),
            _ => new BrScoringSettings([10, 6, 5, 4, 3, 2, 1, 1], 1, null),
        };
    }

    private static BrMapMode? ParseMapMode(string? rawMode) =>
        ParseMapModePublic(rawMode);

    public static BrMapMode? ParseMapModePublic(string? rawMode)
    {
        return rawMode?.Trim().ToLowerInvariant() switch
        {
            "none" => BrMapMode.None,
            "fixed_stage" => BrMapMode.FixedStage,
            "per_round" => BrMapMode.PerRound,
            "rotation" => BrMapMode.Rotation,
            _ => null,
        };
    }

    private static JsonElement ResolveBrSettingsRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(root, "brSettings", out var brSettings)
            && brSettings.ValueKind == JsonValueKind.Object)
        {
            return brSettings;
        }

        return root;
    }

    private static int[] TryReadPlacementArray(JsonElement scoringElement)
    {
        if (!TryGetPropertyIgnoreCase(scoringElement, "placements", out var placementsElement)
            || placementsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var placements = new List<int>();
        foreach (var item in placementsElement.EnumerateArray())
        {
            if (!item.TryGetInt32(out var value) || value < 0)
                return [];
            placements.Add(value);
        }

        return placements.ToArray();
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var arrayEl)
            || arrayEl.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }

        return values;
    }

    private static bool TryParseJsonElement(object? rawValue, out JsonElement element)
    {
        switch (rawValue)
        {
            case JsonElement jsonElement:
                element = jsonElement.Clone();
                return true;
            case JsonDocument jsonDocument:
                element = jsonDocument.RootElement.Clone();
                return true;
            case string jsonText when !string.IsNullOrWhiteSpace(jsonText):
                try
                {
                    using var parsed = JsonDocument.Parse(jsonText);
                    element = parsed.RootElement.Clone();
                    return true;
                }
                catch
                {
                    element = default;
                    return false;
                }
        }

        element = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
