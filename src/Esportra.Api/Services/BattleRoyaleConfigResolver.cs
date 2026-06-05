using System.Text.Json;

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
        var fallbackPresetKey = BrCatalogBrConfigHelper.ReadDefaultPreset(catalogBrConfig);
        var fallback = ResolvePresetScoring(fallbackPresetKey);

        if (!TryParseJsonElement(stageConfig, out var stageRoot)
            || !TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            || brSection.ValueKind != JsonValueKind.Object)
        {
            return ResolveTournamentScoring(tournamentSettings, fallbackPresetKey, fallback, catalogBrConfig);
        }

        if (TryGetPropertyIgnoreCase(brSection, "scoring", out var stageScoring)
            && stageScoring.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(stageScoring, "custom", out var customScoring)
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
            if (TryGetPropertyIgnoreCase(stageScoring, "presetKey", out var presetEl)
                && presetEl.ValueKind == JsonValueKind.String)
            {
                presetKey = presetEl.GetString();
            }

            var resolved = ResolvePresetScoring(presetKey ?? fallbackPresetKey);
            if (TryGetPropertyIgnoreCase(stageScoring, "killCap", out var stageKillCapEl)
                && stageKillCapEl.ValueKind != JsonValueKind.Null
                && stageKillCapEl.TryGetInt32(out var stageKillCap)
                && stageKillCap > 0)
            {
                resolved = resolved with { KillCap = stageKillCap };
            }

            return resolved;
        }

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

    public static int? ResolveGameCount(object? tournamentSettings, object? stageConfig)
    {
        if (TryParseJsonElement(stageConfig, out var stageRoot)
            && TryGetPropertyIgnoreCase(stageRoot, "br", out var brSection)
            && brSection.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(brSection, "gameCount", out var gameCountEl)
            && gameCountEl.TryGetInt32(out var stageGameCount)
            && stageGameCount > 0)
        {
            return stageGameCount;
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
        var pointsComparison = b.TotalPoints.CompareTo(a.TotalPoints);
        if (pointsComparison != 0)
            return pointsComparison;

        return tiebreaker switch
        {
            BrTiebreaker.MostWins => CompareMostWins(a, b),
            BrTiebreaker.MostKills => CompareMostKills(a, b),
            BrTiebreaker.HeadToHead => CompareHeadToHead(a, b),
            _ => CompareMostWins(a, b),
        };
    }

    private static int CompareMostWins(BrLeaderboardAggregate a, BrLeaderboardAggregate b)
    {
        var winsComparison = b.Wins.CompareTo(a.Wins);
        if (winsComparison != 0)
            return winsComparison;

        var killsComparison = b.TotalKills.CompareTo(a.TotalKills);
        if (killsComparison != 0)
            return killsComparison;

        return a.AvgPlacement.CompareTo(b.AvgPlacement);
    }

    private static int CompareMostKills(BrLeaderboardAggregate a, BrLeaderboardAggregate b)
    {
        var killsComparison = b.TotalKills.CompareTo(a.TotalKills);
        if (killsComparison != 0)
            return killsComparison;

        var winsComparison = b.Wins.CompareTo(a.Wins);
        if (winsComparison != 0)
            return winsComparison;

        return a.AvgPlacement.CompareTo(b.AvgPlacement);
    }

    private static int CompareHeadToHead(BrLeaderboardAggregate a, BrLeaderboardAggregate b)
    {
        var placementComparison = a.AvgPlacement.CompareTo(b.AvgPlacement);
        if (placementComparison != 0)
            return placementComparison;

        var winsComparison = b.Wins.CompareTo(a.Wins);
        if (winsComparison != 0)
            return winsComparison;

        return b.TotalKills.CompareTo(a.TotalKills);
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
