using System.Security.Cryptography;
using System.Text;

namespace Esportra.Core.Air;

/// <summary>Game-specific capabilities are resolved here, never inferred from a template default.</summary>
public static class AirGamePacks
{
    public static string? Resolve(string? game) => game?.Trim().ToLowerInvariant() switch
    {
        "valorant" => "esportra-valorant-v1",
        _ => null
    };

    public static readonly (string Overlay, string Title, string Type)[] ValorantScreens =
    [
        ("starting-soon", "Opening", "pre_show"),
        ("match-intro", "Match Introduction", "pre_show"),
        ("ingame", "Gameplay", "match"),
        ("brb", "Break", "break"),
        ("series", "Results", "post_show")
    ];

    public static Guid ScreenId(Guid packageId, string overlay) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{packageId}:{overlay}"))[..16]);
}
