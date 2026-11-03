using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Services.ProviderMatches.Career;

namespace Esportra.Api.Services.ScrimData;

/// <summary>
/// Merges confirmed scrim maps with whatever data each has into one list. Pure: the same inputs give the same list.
/// A map's tier is decided by what can be trusted, never by what was claimed: Riot data only counts when its association was
/// verified, screenshot data only when a scoreboard was stored, and everything else still counts for the result and the map.
/// </summary>
public static class ScrimDataBuilder
{
    private static readonly string[] RiotNotVerified = ["disputed", "unverified", "duplicate", "unavailable"];

    public static ScrimDataSet Build(
        IReadOnlyList<ScrimMapRow> rows, IReadOnlyDictionary<Guid, RiotGameRow> riotGames, IReadOnlyList<RiotPlayerRow> riotPlayers,
        int awaitingConfirmation = 0, int disputed = 0)
    {
        var playersByGame = riotPlayers.GroupBy(p => p.GameId).ToDictionary(g => g.Key, g => g.ToList());
        var games = rows.Select(r => BuildGame(r, riotGames.GetValueOrDefault(r.GameId), playersByGame.GetValueOrDefault(r.GameId) ?? [])).ToList();
        return new ScrimDataSet(games, CoverageOf(rows, games, awaitingConfirmation, disputed));
    }

    public static string Result(ScrimMapRow r) =>
        r.WinnerSlot is { } w ? (w == r.TeamSlot ? ResultCode.Win : ResultCode.Loss)
        : Mine(r) > Theirs(r) ? ResultCode.Win : Mine(r) < Theirs(r) ? ResultCode.Loss : ResultCode.Draw;

    private static int Mine(ScrimMapRow r) => r.TeamSlot == 1 ? r.Team1Score : r.Team2Score;
    private static int Theirs(ScrimMapRow r) => r.TeamSlot == 1 ? r.Team2Score : r.Team1Score;

    /// <summary>One spelling per map, so a Riot internal name and a typed name land in the same row.</summary>
    public static string? CanonicalMap(string? typed, string? riotMapId)
    {
        if (ProviderMatchVerifier.DisplayMap(riotMapId) is { } fromRiot) return fromRiot;
        var name = typed?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        return ProviderMatchVerifier.KnownMapNames.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    private static ScrimGame BuildGame(ScrimMapRow row, RiotGameRow? riot, List<RiotPlayerRow> riotPlayers)
    {
        var rounds = row.Team1Score + row.Team2Score;
        var riotUsable = riot is not null && row.AssociationStatus is "verified" or "partially_verified";
        var screenshot = !riotUsable && row.Source == "ocr" ? OcrScoreboard.TeamLines(row.GameId, row.OcrMatchData, row.TeamSlot, rounds) : [];

        var tier = riotUsable ? DataTier.Full : screenshot.Count > 0 ? DataTier.Screenshot : DataTier.ScoreOnly;
        var lines = tier == DataTier.Full ? riotPlayers.Select(ToLine).ToList() : screenshot.ToList();
        var map = CanonicalMap(row.MapName, riotUsable ? riot!.MapId : null);
        var agents = lines.Select(l => l.CharacterId).Where(a => a is not null).Select(a => a!).OrderBy(a => a, StringComparer.Ordinal).ToArray();

        var detail = new TeamGameDetail(
            row.GameId, row.PlayedAt, row.GameNumber, map, Mine(row), Theirs(row),
            tier == DataTier.Full ? riot!.RoundsPlayed : rounds, agents,
            tier == DataTier.Full ? riot!.AttackPlayed : 0, tier == DataTier.Full ? riot!.AttackWon : 0,
            tier == DataTier.Full ? riot!.DefensePlayed : 0, tier == DataTier.Full ? riot!.DefenseWon : 0);
        return new ScrimGame(row, tier, Result(row), map, detail, lines);
    }

    private static ScrimPlayerLine ToLine(RiotPlayerRow p)
    {
        var shots = p.Headshots + p.Bodyshots + p.Legshots;
        return new ScrimPlayerLine(
            p.GameId, DataTier.Full, p.UserId, p.Name, p.CharacterId?.ToLowerInvariant(), p.MemberRole, p.Kills, p.Deaths, p.Assists,
            p.RoundsPlayed > 0 ? Math.Round((double)p.Score / p.RoundsPlayed, 1) : null, p.RoundsPlayed,
            p.RoundsPlayed > 0 ? Math.Round((double)p.DamageDealt / p.RoundsPlayed, 1) : null,
            shots > 0 ? Math.Round(100.0 * p.Headshots / shots, 1) : null, p.FirstKills, p.FirstDeaths, p.Plants, p.Defuses);
    }

    private static Coverage CoverageOf(IReadOnlyList<ScrimMapRow> rows, IReadOnlyList<ScrimGame> games, int awaiting, int disputed)
    {
        var fell = rows.Zip(games).Where(x => x.Second.Tier != DataTier.Full && x.First.Source == "riot").Select(x => x.First).ToList();
        var notVerified = fell.Count(r => (r.AssociationStatus is { } s && RiotNotVerified.Contains(s)) || r.MatchDataStatus is "failed" or "unavailable");
        return new Coverage(
            games.Count, games.Count(g => g.Tier == DataTier.Full), games.Count(g => g.Tier == DataTier.Screenshot),
            games.Count(g => g.Tier == DataTier.ScoreOnly), fell.Count - notVerified, notVerified, awaiting, disputed,
            games.Count(g => g.Tier == DataTier.Full && g.Row.IsCustom == false));
    }
}
