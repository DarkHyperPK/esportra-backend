using System.Text.RegularExpressions;

namespace Esportra.Core.Broadcast;

public sealed record BroadcastTeamAppearance(string? DisplayName = null, string? Color = null);
public sealed record BroadcastAppearance(
    string Pack = "esportra", string BannerTheme = "broadcast", bool TeamColors = true,
    bool EventBug = true, bool SponsorRotation = true, int SponsorSeconds = 8,
    Dictionary<Guid, BroadcastTeamAppearance[]>? Matches = null, int Revision = 0, string? StingerName = null)
{
    public string? Validate()
    {
        if (Pack != "esportra") return "Choose the installed Esportra HUD pack.";
        if (Revision < 0) return "Invalid appearance revision.";
        if (BannerTheme is not ("broadcast" or "compact" or "monument" or "impact")) return "Choose an available banner theme.";
        if (SponsorSeconds is < 5 or > 120) return "Sponsor rotation must be between 5 and 120 seconds.";
        if (StingerName is not null && (string.IsNullOrWhiteSpace(StingerName) || StingerName.Length > 256 || StingerName.Any(char.IsControl))) return "Enter an existing OBS Stinger transition name, or leave it empty.";
        if (Matches?.Count > 200) return "Too many match overrides.";
        foreach (var (id, teams) in Matches ?? [])
        {
            if (id == Guid.Empty || teams is null || teams.Length != 2) return "Each match needs two team display settings.";
            foreach (var team in teams)
            {
                if (team is null) return "Team display settings are required.";
                if (team.DisplayName is { } name && (name.Length > 80 || name.Any(char.IsControl) || name.IndexOfAny(['<', '>']) >= 0)) return "Display names must be plain text of at most 80 characters without markup.";
                if (team.Color is { } color && !Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)) return "Team colors must use #RRGGBB.";
            }
        }
        return null;
    }

    public BroadcastSegmentPlan ForNewPlan(BroadcastSegmentPlan plan) =>
        StingerName is null ? plan : plan with { Transition = new("stinger", StingerName) };
}
