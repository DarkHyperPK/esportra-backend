using System.Globalization;
using System.Text;
using System.Text.Json;
using Esportra.Api.Services;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>Per-player outcome of checking a screenshot report against the two rosters.</summary>
public static class VerificationStatus
{
    /// <summary>
    /// The name read from the screenshot matches a game name linked through Riot sign-in. Screenshots
    /// don't show the #tag, so this alone isn't proof of the account: <see cref="RiotStatus.Confirmed"/> is.
    /// </summary>
    public const string Verified = "verified";
    /// <summary>Matched a Riot tag the player typed themselves (not proven).</summary>
    public const string Claimed = "claimed";
    /// <summary>Matched only an Esportra username.</summary>
    public const string UsernameOnly = "username_only";
    /// <summary>Name unreadable; assigned as the team's only unmatched player.</summary>
    public const string Unconfirmed = "unconfirmed";
    /// <summary>The captain changed the name the screenshot showed.</summary>
    public const string Edited = "edited";
    /// <summary>On the roster of the other competitor.</summary>
    public const string OtherTeam = "other_team";
    /// <summary>The same player appears on two rows.</summary>
    public const string Duplicate = "duplicate";
    /// <summary>Not on either roster.</summary>
    public const string Unknown = "unknown";

    /// <summary>Statuses that notify organizers and are shown to the opponent as dispute reasons.</summary>
    public static readonly IReadOnlySet<string> NeedsReview = new HashSet<string> { Unknown, OtherTeam, Duplicate, Edited };
}

/// <summary>Per-player outcome of checking the report against Riot's own record of the match (by PUUID).</summary>
public static class RiotStatus
{
    /// <summary>The player's linked Riot account played this match.</summary>
    public const string Confirmed = "confirmed";
    /// <summary>The player has a linked account, but a different account with the same game name played.</summary>
    public const string OtherAccount = "other_account";
    /// <summary>The player has a linked account that isn't in the match, and no one with this name is.</summary>
    public const string NotInMatch = "not_in_match";
    /// <summary>The account that played under this name isn't linked to anyone on either roster.</summary>
    public const string Unregistered = "unregistered";

    public static readonly IReadOnlySet<string> NeedsReview = new HashSet<string> { OtherAccount, NotInMatch, Unregistered };
}

/// <summary>Where the Riot cross-check of a report stands.</summary>
public static class RiotCheckStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    /// <summary>No matching custom game in the players' Riot history: names were checked from the screenshot only.</summary>
    public const string NotFound = "not_found";
    /// <summary>The API key can't read Valorant matches.</summary>
    public const string Unavailable = "unavailable";
}

public sealed record RiotCheck(string Status, string? MatchId = null, DateTimeOffset? CheckedAt = null, bool AutoAccepted = false);

public sealed record VerifiedPlayer(
    int Index, string Name, string? Team, string? UserId, string Status, string? Source, string? ReadName,
    string? RiotStatus = null, string? RiotId = null, string? RiotKda = null);

public sealed record OcrVerification(IReadOnlyList<VerifiedPlayer> Players, IReadOnlyList<string> Flags, RiotCheck? Riot = null)
{
    public IReadOnlyList<VerifiedPlayer> NeedsReview =>
        Players.Where(p => VerificationStatus.NeedsReview.Contains(p.Status) || (p.RiotStatus is { } r && RiotStatus.NeedsReview.Contains(r))).ToList();
}

/// <summary>A linked Riot account of a roster member.</summary>
public sealed record LinkedAccount(string UserId, string Team, string Puuid);

/// <summary>camelCase JSON for the stored and returned verification.</summary>
public static class OcrVerificationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>A submitted row: the name, team and K/D/A the captain confirmed.</summary>
public sealed record SubmittedPlayer(string Name, string? Team, int? Kills = null, int? Deaths = null, int? Assists = null);

/// <summary>
/// Decides, per player, whether a screenshot report names the people on the competitors' rosters.
/// Pure: the API runs it at parse time (shown to the captain) and again at submit time from the stored
/// parse, the submitted names and a fresh roster, so nothing the client sends is trusted.
/// </summary>
public static class ScoreboardVerification
{
    private sealed record ParsedRow(string? ReadName, string? Shown, string? Side, string? UserId, string? MatchTeam, string? Source, string? Method);

    /// <summary>The OCR read itself: each row as read, its team from the ally/enemy side.</summary>
    public static OcrVerification FromParse(JsonElement result, OcrRosters rosters)
    {
        var rows = ReadRows(result);
        var allyTeam = ReadAllyTeam(result);
        var submitted = rows.Select(r => new SubmittedPlayer(r.Shown ?? r.ReadName ?? "", TeamOf(r.Side, allyTeam))).ToList();
        return Verify(result, submitted, rosters);
    }

    public static OcrVerification Verify(JsonElement result, IReadOnlyList<SubmittedPlayer> submitted, OcrRosters rosters)
    {
        var rows = ReadRows(result);
        var players = submitted.Select((player, i) => Classify(i, player, i < rows.Count ? rows[i] : null, rosters)).ToList();
        var duplicates = players.Where(p => p.UserId is not null).GroupBy(p => p.UserId).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToHashSet();
        players = players.Select(p => duplicates.Contains(p) ? p with { Status = VerificationStatus.Duplicate } : p).ToList();
        return new OcrVerification(players, []);
    }

    public const string FlagStatsMismatch = "stats_mismatch";
    public const string FlagScoreMismatch = "score_mismatch";

    /// <summary>
    /// Checks a report against Riot's record of the match, which lists every player's PUUID and full
    /// Riot ID (the screenshot shows only the game name). Each row is confirmed by its player's linked
    /// account, or flagged: a different account under the same name, a linked player who didn't play,
    /// or an account no one on either roster has linked. K/D/A and the score are compared too.
    /// </summary>
    public static OcrVerification ApplyRiotMatch(
        OcrVerification verification, IReadOnlyList<LinkedAccount> linked, RiotMatch match,
        IReadOnlyList<SubmittedPlayer> submitted, (int Team1, int Team2)? score, DateTimeOffset now)
    {
        var owners = linked.GroupBy(a => a.Puuid).ToDictionary(g => g.Key, g => g.First());
        var flags = verification.Flags.ToList();
        var players = verification.Players.Select(p =>
        {
            var (checkedPlayer, riotPlayer) = MatchToRiot(p, linked, owners, match);
            var stats = p.Index < submitted.Count ? submitted[p.Index] : null;
            if (riotPlayer is null || stats is null || SameKda(stats, riotPlayer)) return checkedPlayer;
            if (!flags.Contains(FlagStatsMismatch)) flags.Add(FlagStatsMismatch);
            return checkedPlayer with { RiotKda = $"{riotPlayer.Kills}/{riotPlayer.Deaths}/{riotPlayer.Assists}" };
        }).ToList();

        var blueTeam = MajorityTeam(match, "Blue", owners);
        var redTeam = MajorityTeam(match, "Red", owners);
        if (score is { } submittedScore && blueTeam is not null && redTeam is not null && blueTeam != redTeam)
        {
            var riotScore = blueTeam == "team1" ? (match.BlueRounds, match.RedRounds) : (match.RedRounds, match.BlueRounds);
            if (riotScore != submittedScore) flags.Add(FlagScoreMismatch);
        }
        return verification with
        {
            Players = players,
            Flags = flags,
            Riot = new RiotCheck(RiotCheckStatus.Confirmed, match.MatchId, now),
        };
    }

    /// <summary>
    /// The player check of a report taken straight from Riot ("Auto-fetch", or a screenshot that matched a
    /// Riot game): one row per Riot player, on the roster when their PUUID is linked to a member.
    /// </summary>
    public static OcrVerification FromRiotMatch(RiotMatch match, IReadOnlyList<LinkedAccount> linked, RiotCheck? riot)
    {
        var owners = linked.GroupBy(a => a.Puuid).ToDictionary(g => g.Key, g => g.First());
        var (blue, red) = ScoreboardRiotMatcher.SideTeams(match, linked);
        var players = match.Players.Select((p, i) =>
        {
            var team = p.TeamId == "Blue" ? blue : red;
            return owners.TryGetValue(p.Puuid, out var owner)
                ? new VerifiedPlayer(i, p.GameName, team ?? owner.Team, owner.UserId, VerificationStatus.Verified, OcrNameSource.RiotVerified, p.GameName)
                : new VerifiedPlayer(i, p.GameName, team, null, VerificationStatus.Unknown, null, p.GameName);
        }).ToList();
        return new OcrVerification(players, [], riot);
    }

    /// <summary>
    /// Riot's record backs the whole report: every row is a linked account that played, nothing needs a
    /// look, nothing differs from Riot (score, K/D/A), and both Riot sides map to the two bracket teams.
    /// Such a report doesn't need the opponent to confirm it.
    /// </summary>
    public static bool IsFullyConfirmed(OcrVerification verification, RiotMatch match, IReadOnlyList<LinkedAccount> linked) =>
        verification.Riot?.Status == RiotCheckStatus.Confirmed
        && verification.Players.Count > 0
        && verification.Players.Count == match.Players.Count
        && verification.Players.All(p => p.RiotStatus == RiotStatus.Confirmed)
        && verification.NeedsReview.Count == 0
        && verification.Flags.Count == 0
        && ScoreboardRiotMatcher.SideTeams(match, linked) is ({ } blue, { } red) && blue != red;

    private static (VerifiedPlayer Player, RiotMatchPlayer? Riot) MatchToRiot(
        VerifiedPlayer player, IReadOnlyList<LinkedAccount> linked, Dictionary<string, LinkedAccount> owners, RiotMatch match)
    {
        var key = NameKey.Of(player.Name);
        var byName = key.Length == 0 ? null : match.Players.FirstOrDefault(r => NameKey.Of(r.GameName) == key);
        if (player.UserId is { } userId)
        {
            var mine = linked.Where(a => a.UserId == userId).Select(a => a.Puuid).ToHashSet();
            if (match.Players.FirstOrDefault(r => mine.Contains(r.Puuid)) is { } played)
                return (player with { RiotStatus = RiotStatus.Confirmed, RiotId = played.RiotId }, played);
            if (mine.Count > 0)
            {
                return byName is null
                    ? (player with { RiotStatus = RiotStatus.NotInMatch }, null)
                    : (player with { RiotStatus = RiotStatus.OtherAccount, RiotId = byName.RiotId }, byName);
            }
        }
        if (byName is null) return (player, null);
        if (owners.TryGetValue(byName.Puuid, out var owner))
        {
            // Riot knows whose account this is even when the screenshot name didn't match the roster.
            var status = player.Team is not null && player.Team != owner.Team ? VerificationStatus.OtherTeam : VerificationStatus.Verified;
            return (player with { UserId = owner.UserId, Status = status, RiotStatus = RiotStatus.Confirmed, RiotId = byName.RiotId }, byName);
        }
        return (player with { RiotStatus = RiotStatus.Unregistered, RiotId = byName.RiotId }, byName);
    }

    private static bool SameKda(SubmittedPlayer submitted, RiotMatchPlayer riot) =>
        (submitted.Kills is null || submitted.Kills == riot.Kills)
        && (submitted.Deaths is null || submitted.Deaths == riot.Deaths)
        && (submitted.Assists is null || submitted.Assists == riot.Assists);

    /// <summary>The bracket team most of a Riot side's linked players belong to.</summary>
    private static string? MajorityTeam(RiotMatch match, string side, Dictionary<string, LinkedAccount> owners) =>
        match.Players.Where(p => p.TeamId == side && owners.ContainsKey(p.Puuid))
            .GroupBy(p => owners[p.Puuid].Team)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    private static VerifiedPlayer Classify(int index, SubmittedPlayer player, ParsedRow? read, OcrRosters rosters)
    {
        var name = player.Name.Trim();
        var key = NameKey.Of(name);
        var asRead = read is not null && !string.IsNullOrWhiteSpace(read.ReadName) && key == NameKey.Of(read.ReadName);
        VerifiedPlayer Make(string status, string? userId, string? source) => new(index, name, player.Team, userId, status, source, read?.ReadName);

        // The OCR matched this row and the captain kept that player (as read, or the roster spelling).
        if (read?.UserId is { } userId && (asRead || HasName(rosters, userId, key)))
        {
            var status = read.Method == "elimination" ? VerificationStatus.Unconfirmed : StatusOfSource(read.Source);
            return Teamed(Make(status, userId, read.Source), read.MatchTeam);
        }

        // Otherwise look the confirmed name up. A name the captain typed or changed is "edited".
        var found = FindOnRoster(key, rosters);
        if (found is null) return Make(VerificationStatus.Unknown, null, null);
        var typed = !asRead;
        return Teamed(Make(typed ? VerificationStatus.Edited : StatusOfSource(found.Value.Source), found.Value.UserId, found.Value.Source), found.Value.Team);
    }

    private static bool HasName(OcrRosters rosters, string userId, string key) =>
        key.Length > 0 && AllPlayers(rosters).Any(p => p.Player.UserId == userId && p.Player.Candidates.Any(c => NameKey.Of(c.Name) == key));

    private static VerifiedPlayer Teamed(VerifiedPlayer player, string? rosterTeam) =>
        player.Team is not null && rosterTeam is not null && player.Team != rosterTeam
            ? player with { Status = VerificationStatus.OtherTeam }
            : player;

    private static string StatusOfSource(string? source) => source switch
    {
        OcrNameSource.RiotVerified or OcrNameSource.RiotPrevious => VerificationStatus.Verified,
        OcrNameSource.RiotClaimed => VerificationStatus.Claimed,
        _ => VerificationStatus.UsernameOnly,
    };

    /// <summary>An exact name match, else a one-edit match when exactly one player fits ("Player1" vs
    /// "Player2" on the same roster never resolves to the wrong one).</summary>
    private static (string UserId, string Team, string Source)? FindOnRoster(string key, OcrRosters rosters)
    {
        if (key.Length == 0) return null;
        var hits = AllPlayers(rosters)
            .SelectMany(p => p.Player.Candidates.Select(c => (p.Player.UserId, p.Team, c.Source, Key: NameKey.Of(c.Name))))
            .ToList();
        var exact = hits.FirstOrDefault(h => h.Key == key);
        if (exact.UserId is not null) return (exact.UserId, exact.Team, exact.Source);
        var near = hits.Where(h => NameKey.Close(key, h.Key)).ToList();
        return near.Select(h => h.UserId).Distinct().Count() == 1 ? (near[0].UserId, near[0].Team, near[0].Source) : null;
    }

    private static IEnumerable<(OcrRosterPlayer Player, string Team)> AllPlayers(OcrRosters rosters) =>
        rosters.Team1.Select(p => (p, "team1")).Concat(rosters.Team2.Select(p => (p, "team2")));

    private static string? TeamOf(string? side, string? allyTeam) => (side, allyTeam) switch
    {
        ("ally", "team1") or ("enemy", "team2") => "team1",
        ("ally", "team2") or ("enemy", "team1") => "team2",
        _ => null,
    };

    private static string? ReadAllyTeam(JsonElement result) =>
        result.TryGetProperty("allyTeam", out var ally) && ally.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<ParsedRow> ReadRows(JsonElement result)
    {
        if (!result.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return [];
        return players.EnumerateArray().Select(p =>
        {
            var match = p.TryGetProperty("rosterMatch", out var m) && m.ValueKind == JsonValueKind.Object ? m : (JsonElement?)null;
            var shown = p.TryGetProperty("name", out var n) ? Str(n, "value") : null;
            return new ParsedRow(
                Str(p, "nameRead"),
                shown,
                Str(p, "side"),
                match is { } a ? Str(a, "userId") : null,
                match is { } b ? Str(b, "team") : null,
                match is { } c ? Str(c, "source") : null,
                match is { } d ? Str(d, "method") ?? "read" : null);
        }).ToList();
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Name comparison key, the same idea as the OCR service's: case, spacing, punctuation and
/// diacritics ignored, any script kept, OCR look-alikes (O/0, l/1/I, S/5, Z/2) folded.
/// </summary>
public static class NameKey
{
    public static string Of(string name)
    {
        var text = name.Split('#', 2)[0].Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark || !char.IsLetterOrDigit(ch)) continue;
            builder.Append(ch switch { 'o' => '0', 'l' or 'i' => '1', 's' => '5', 'z' => '2', _ => ch });
        }
        return builder.ToString();
    }

    /// <summary>Equal keys, or one edit apart for names of five or more characters.</summary>
    public static bool Close(string a, string b)
    {
        if (a == b) return a.Length > 0;
        if (Math.Min(a.Length, b.Length) < 5 || Math.Abs(a.Length - b.Length) > 1) return false;
        return Distance(a, b) <= 1;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
