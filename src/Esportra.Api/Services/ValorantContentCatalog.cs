using System.Text.Json;

namespace Esportra.Api.Services;

/// <summary>Static Valorant content ID → display name/icon resolution for Riot match payloads.</summary>
internal static class ValorantContentCatalog
{
    private static readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/Game/Maps/Ascent/Ascent"] = "Ascent",
        ["/Game/Maps/Duality/Duality"] = "Bind",
        ["/Game/Maps/Triad/Triad"] = "Haven",
        ["/Game/Maps/Bonsai/Bonsai"] = "Split",
        ["/Game/Maps/Port/Port"] = "Icebox",
        ["/Game/Maps/Foxtrot/Foxtrot"] = "Breeze",
        ["/Game/Maps/Canyon/Canyon"] = "Fracture",
        ["/Game/Maps/Pitt/Pitt"] = "Pearl",
        ["/Game/Maps/Jam/Jam"] = "Lotus",
        ["/Game/Maps/Juliett/Juliett"] = "Sunset",
        ["/Game/Maps/Infinity/Infinity"] = "Abyss",
        ["/Game/Maps/Rook/Rook"] = "Corrode",
        ["/Game/Maps/HURM/HURM_Alley/HURM_Alley"] = "District",
        ["/Game/Maps/HURM/HURM_Bowl/HURM_Bowl"] = "Kasbah",
        ["/Game/Maps/HURM/HURM_Yard/HURM_Yard"] = "Piazza",
    };

    private static readonly Dictionary<string, (string Name, string IconUrl)> Weapons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["9c82e19d-4575-0200-1a81-3eacf00cf872"] = ("Vandal", WeaponIcon("9c82e19d-4575-0200-1a81-3eacf00cf872")),
            ["ee8e8d15-496b-0a37-892d-5d53cf525927"] = ("Phantom", WeaponIcon("ee8e8d15-496b-0a37-892d-5d53cf525927")),
            ["031cf7d1-4136-7e44-ace4-95a4bf4b6dd9"] = ("Bulldog", WeaponIcon("031cf7d1-4136-7e44-ace4-95a4bf4b6dd9")),
            ["462080d1-4035-2937-7c09-27aa2e5c27a7"] = ("Spectre", WeaponIcon("462080d1-4035-2937-7c09-27aa2e5c27a7")),
            ["ae3de142-4d85-2547-dd26-4e90bed35cf7"] = ("Guardian", WeaponIcon("ae3de142-4d85-2547-dd26-4e90bed35cf7")),
            ["55d8a0f4-4274-ca67-fe2c-06ab45efdf58"] = ("Classic", WeaponIcon("55d8a0f4-4274-ca67-fe2c-06ab45efdf58")),
            ["29a0cfab-485b-f05d-779a-b59f85e674a6"] = ("Shorty", WeaponIcon("29a0cfab-485b-f05d-779a-b59f85e674a6")),
            ["1baa85b4-4c70-1284-64bb-6481df429b75"] = ("Ghost", WeaponIcon("1baa85b4-4c70-1284-64bb-6481df429b75")),
            ["f7e1b454-4ad4-1063-8870-7a152a9fd947"] = ("Sheriff", WeaponIcon("f7e1b454-4ad4-1063-8870-7a152a9fd947")),
            ["42da8ccc-40c5-affc-beec-15aa47b42eda"] = ("Stinger", WeaponIcon("42da8ccc-40c5-affc-beec-15aa47b42eda")),
            ["a03b24d3-4319-996d-0f8c-94aaecebf951"] = ("Marshal", WeaponIcon("a03b24d3-4319-996d-0f8c-94aaecebf951")),
            ["4880e975-4294-b138-8a49-a4cb5ac5ff85"] = ("Operator", WeaponIcon("4880e975-4294-b138-8a49-a4cb5ac5ff85")),
            ["910be174-449b-c412-ab22-0871470e789b"] = ("Ares", WeaponIcon("910be174-449b-c412-ab22-0871470e789b")),
            ["63e6c2b6-4a8e-869c-3d4c-e38355226584"] = ("Odin", WeaponIcon("63e6c2b6-4a8e-869c-3d4c-e38355226584")),
            ["e336c6b8-418d-9340-d77f-7a9e4cfe0702"] = ("Judge", WeaponIcon("e336c6b8-418d-9340-d77f-7a9e4cfe0702")),
            ["f454e7b6-4144-03b9-0b5c-499261b6689a"] = ("Bucky", WeaponIcon("f454e7b6-4144-03b9-0b5c-499261b6689a")),
        };

    internal static string ResolveMapName(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId)) return "Unknown Map";
        if (Maps.TryGetValue(mapId, out var name)) return name;
        var lastSlash = mapId.LastIndexOf('/');
        return lastSlash >= 0 ? mapId[(lastSlash + 1)..] : mapId;
    }

    internal static string ResolveGameMode(string? gameMode, string? queueId, bool isRanked)
    {
        var queue = queueId ?? string.Empty;
        if (queue.Contains("competitive", StringComparison.OrdinalIgnoreCase)) return "Competitive";
        if (queue.Contains("unrated", StringComparison.OrdinalIgnoreCase)) return "Unrated";
        if (queue.Contains("swiftplay", StringComparison.OrdinalIgnoreCase)) return "Swiftplay";
        if (queue.Contains("spikerush", StringComparison.OrdinalIgnoreCase)) return "Spike Rush";
        if (queue.Contains("deathmatch", StringComparison.OrdinalIgnoreCase)) return "Deathmatch";
        if (queue.Contains("ggteam", StringComparison.OrdinalIgnoreCase)) return "Escalation";
        if (queue.Contains("hurm", StringComparison.OrdinalIgnoreCase)) return "Team Deathmatch";
        if (gameMode?.Contains("Bomb", StringComparison.OrdinalIgnoreCase) == true)
            return isRanked ? "Competitive" : "Standard";
        return "Custom";
    }

    internal static (string Name, string? IconUrl) ResolveWeapon(string? weaponId)
    {
        if (string.IsNullOrWhiteSpace(weaponId)) return ("Unknown", null);
        if (Weapons.TryGetValue(weaponId, out var weapon)) return weapon;
        if (!weaponId.Contains('-')) return (weaponId, null);
        return ("Unknown Weapon", null);
    }

    internal static (string Name, string? IconUrl) ResolveAgent(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId)) return ("Unknown", null);
        return ("Agent", AgentIcon(characterId));
    }

    internal static string ResolveRoundResultLabel(string? resultCode) => resultCode switch
    {
        "Elimination" => "Elimination",
        "Detonate" => "Spike Detonated",
        "Defuse" => "Spike Defused",
        "TimeOut" => "Time Expired",
        _ => resultCode ?? "Round End",
    };

    /// <summary>Riot roundNum is 0-based; convert to 1-based display round.</summary>
    internal static int ResolveDisplayRound(JsonElement round, int fallbackOneBased)
    {
        if (round.TryGetProperty("roundNum", out var roundNumEl) && roundNumEl.TryGetInt32(out var roundNum))
            return roundNum + 1;
        return fallbackOneBased;
    }

    private static string AgentIcon(string uuid) =>
        $"https://media.valorant-api.com/agents/{uuid}/displayicon.png";

    private static string WeaponIcon(string uuid) =>
        $"https://media.valorant-api.com/weapons/{uuid}/displayicon.png";
}
