using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Esportra.Api.Services;

public static class GameCatalogHashHelper
{
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string ComputeHash(string catalogVersion, int schemaVersion, IReadOnlyList<CatalogGameHashInput> games)
    {
        var payload = new
        {
            catalogVersion,
            schemaVersion,
            games = games
                .OrderBy(g => g.Slug, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    g.Slug,
                    g.Name,
                    g.Category,
                    gameType = g.GameType,
                    defaultModeKey = g.DefaultModeKey,
                    g.Features,
                    g.BrConfig,
                    g.LogoUrl,
                    g.IconUrl,
                    g.CoverUrl,
                    g.SortOrder,
                    modes = g.Modes
                        .OrderBy(m => m.ModeKey, StringComparer.OrdinalIgnoreCase)
                        .Select(m => new
                        {
                            modeKey = m.ModeKey,
                            m.Name,
                            teamSize = m.TeamSize,
                            participantMode = m.ParticipantMode,
                            allowsSubstitutes = m.AllowsSubstitutes,
                            maxRosterSize = m.MaxRosterSize,
                            aliases = (m.Aliases ?? Array.Empty<string>()).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray(),
                            modeGroup = m.ModeGroup,
                            variantLabel = m.VariantLabel,
                            mapPoolFilter = m.MapPoolFilter,
                            features = m.Features,
                        }),
                    tournamentStructures = g.Structures
                        .OrderBy(s => s.StructureKey, StringComparer.OrdinalIgnoreCase)
                        .Select(s => new
                        {
                            structureKey = s.StructureKey,
                            s.Name,
                            isDefault = s.IsDefault,
                        }),
                    aliases = (g.Aliases ?? Array.Empty<string>()).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray(),
                }),
        };

        var json = JsonSerializer.Serialize(payload, CanonicalOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

public sealed record CatalogGameHashInput(
    string Slug,
    string Name,
    string? Category,
    string GameType,
    string DefaultModeKey,
    object Features,
    object? BrConfig,
    string? LogoUrl,
    string? IconUrl,
    string? CoverUrl,
    int SortOrder,
    IReadOnlyList<CatalogModeHashInput> Modes,
    IReadOnlyList<CatalogStructureHashInput> Structures,
    IReadOnlyList<string> Aliases);

public sealed record CatalogModeHashInput(
    string ModeKey,
    string Name,
    int TeamSize,
    string ParticipantMode,
    bool AllowsSubstitutes,
    int? MaxRosterSize,
    IReadOnlyList<string> Aliases,
    string? ModeGroup,
    string? VariantLabel,
    string? MapPoolFilter = null,
    object? Features = null);

public sealed record CatalogStructureHashInput(
    string StructureKey,
    string Name,
    bool IsDefault);
