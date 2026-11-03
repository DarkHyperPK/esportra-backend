namespace Esportra.Api.Services.ProviderMatches;

/// <summary>A roster member as frozen when the result was reported. Slot is 1 or 2 (which reporting team).</summary>
public sealed record RosterMember(Guid? UserId, int Slot, string? Puuid);

public sealed record VerificationInput(
    NormalizedValorantMatch Match, int? ReportedTeam1Score, int? ReportedTeam2Score, string? ReportedMap,
    IReadOnlyList<RosterMember> Roster, bool AllowNonCustom = false);

public sealed record PlayerResolution(string Puuid, string RiotSide, int? Slot, Guid? UserId);

public sealed record VerificationResult(
    string Status, string? Team1Side, string? Team2Side, IReadOnlyList<string> Reasons, IReadOnlyList<PlayerResolution> Players);

/// <summary>
/// Decides whether a Riot match really is the game an Esportra team reported. Pure and deterministic. A team's side is
/// found from the linked accounts of its roster; nothing is guessed for players without a linked account.
///   verified           custom, completed, both teams' sides found, scores/map agree, every player is a roster member
///   partially_verified as above but some players are not linked roster members
///   unverified         nothing contradicts the report, but no roster account is in the match
///   disputed           the data contradicts the report (not custom, split team, same side, score or map differs)
///   pending            the match is not finished yet
/// </summary>
public static class ProviderMatchVerifier
{
    public const string Verified = "verified";
    public const string PartiallyVerified = "partially_verified";
    public const string Unverified = "unverified";
    public const string Disputed = "disputed";
    public const string Pending = "pending";

    // Riot's internal map names; unknown ones are not checked rather than guessed.
    private static readonly Dictionary<string, string> MapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Duality"] = "Bind",
        ["Bonsai"] = "Split",
        ["Ascent"] = "Ascent",
        ["Port"] = "Icebox",
        ["Triad"] = "Haven",
        ["Foxtrot"] = "Breeze",
        ["Canyon"] = "Fracture",
        ["Pitt"] = "Pearl",
        ["Jam"] = "Lotus",
        ["Juliett"] = "Sunset",
        ["Infinity"] = "Abyss",
    };

    /// <summary>Display names of the competitive maps this project can identify from Riot's internal names.</summary>
    public static IReadOnlyCollection<string> KnownMapNames => MapNames.Values;

    public static string? DisplayMap(string? mapId)
    {
        var code = mapId?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return code is not null && MapNames.TryGetValue(code, out var name) ? name : null;
    }

    public static VerificationResult Verify(VerificationInput input)
    {
        var match = input.Match;
        var reasons = new List<string>();
        var players = match.Players.Where(p => !p.IsObserver).ToList();
        var bySlot = input.Roster.Where(r => r.Puuid is not null)
            .GroupBy(r => r.Puuid!).ToDictionary(g => g.Key, g => g.First());

        var resolutions = players
            .Select(p => bySlot.TryGetValue(p.Puuid, out var m)
                ? new PlayerResolution(p.Puuid, p.RiotSide, m.Slot, m.UserId)
                : new PlayerResolution(p.Puuid, p.RiotSide, null, null))
            .ToList();

        var disputed = false;
        if (!match.Info.IsCustom)
        {
            if (input.AllowNonCustom) reasons.Add("queue_not_custom");
            else { reasons.Add("not_custom_game"); disputed = true; }
        }

        var sideOf = new Dictionary<int, string>();
        foreach (var slot in new[] { 1, 2 })
        {
            var sides = resolutions.Where(r => r.Slot == slot).Select(r => r.RiotSide).Distinct().ToList();
            if (sides.Count > 1) { reasons.Add($"team{slot}_split_across_sides"); disputed = true; }
            else if (sides.Count == 1) sideOf[slot] = sides[0];
        }
        if (sideOf.Count == 2 && sideOf[1] == sideOf[2]) { reasons.Add("teams_on_same_side"); disputed = true; sideOf.Remove(2); sideOf.Remove(1); }

        var allSides = match.Teams.Select(t => t.TeamId).Distinct().ToList();
        if (allSides.Count == 2 && sideOf.Count == 1 && !disputed)
        {
            var known = sideOf.First();
            sideOf[known.Key == 1 ? 2 : 1] = allSides.First(s => s != known.Value);
        }

        string? side1 = sideOf.GetValueOrDefault(1), side2 = sideOf.GetValueOrDefault(2);
        CheckScore(match, side1, input.ReportedTeam1Score, "team1_score_mismatch", reasons, ref disputed);
        CheckScore(match, side2, input.ReportedTeam2Score, "team2_score_mismatch", reasons, ref disputed);
        CheckMap(match, input.ReportedMap, reasons, ref disputed);

        var result = Decide(match, disputed, side1, side2, resolutions, reasons);
        return new VerificationResult(result, side1, side2, reasons, resolutions);
    }

    private static string Decide(
        NormalizedValorantMatch match, bool disputed, string? side1, string? side2,
        List<PlayerResolution> resolutions, List<string> reasons)
    {
        if (disputed) return Disputed;
        if (!match.Info.IsCompleted) { reasons.Add("match_incomplete"); return Pending; }
        if (side1 is null && side2 is null) { reasons.Add("no_roster_account_in_match"); return Unverified; }
        var unmatched = resolutions.Count(r => r.Slot is null);
        if (unmatched > 0) { reasons.Add($"unmatched_players:{unmatched}"); return PartiallyVerified; }
        return Verified;
    }

    private static void CheckScore(
        NormalizedValorantMatch match, string? side, int? reported, string reason, List<string> reasons, ref bool disputed)
    {
        if (side is null || reported is null) return;
        var team = match.Teams.FirstOrDefault(t => t.TeamId == side);
        var actual = team?.RoundsWon ?? match.Rounds.Count(r => r.WinningTeam == side);
        if (actual != reported) { reasons.Add(reason); disputed = true; }
    }

    private static void CheckMap(NormalizedValorantMatch match, string? reportedMap, List<string> reasons, ref bool disputed)
    {
        var actual = DisplayMap(match.Info.MapId);
        if (actual is null || string.IsNullOrWhiteSpace(reportedMap)) return;
        if (!MapNames.ContainsValue(reportedMap.Trim())) return; // free text we cannot compare
        if (!string.Equals(actual, reportedMap.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("map_mismatch");
            disputed = true;
        }
    }
}
