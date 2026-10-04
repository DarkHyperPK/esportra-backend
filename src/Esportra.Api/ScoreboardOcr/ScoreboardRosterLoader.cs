using System.Data;
using Dapper;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>
/// Name candidates for both match competitors, each tagged with where it came from, so the OCR
/// service can tell a verified Riot ID from a self-typed tag or an Esportra username.
/// </summary>
public static class ScoreboardRosterLoader
{
    private static readonly TimeSpan PreviousNameWindow = TimeSpan.FromDays(60);

    internal sealed record MemberRow(Guid CompetitorId, Guid UserId, string? Username, string? RiotTag);

    internal sealed record RiotNameRow(Guid UserId, string GameName, string Source);

    /// <summary>Active, non-coach members (or the solo participant) of both competitors.</summary>
    public static async Task<IReadOnlyList<Guid>> LoadUserIdsAsync(IDbConnection conn, Guid team1Id, Guid team2Id) =>
        (await LoadMembersAsync(conn, team1Id, team2Id)).Select(m => m.UserId).Distinct().ToList();

    public static async Task<OcrRosters> LoadAsync(IDbConnection conn, Guid team1Id, Guid team2Id)
    {
        var members = await LoadMembersAsync(conn, team1Id, team2Id);
        var userIds = members.Select(m => m.UserId).Distinct().ToArray();
        var riotNames = (await conn.QueryAsync<RiotNameRow>(
            $"""
            SELECT user_id AS UserId, game_name AS GameName, '{OcrNameSource.RiotVerified}' AS Source
            FROM riot_accounts WHERE user_id = ANY(@userIds)
            UNION ALL
            SELECT user_id, game_name,
                   CASE WHEN verified_at IS NOT NULL THEN '{OcrNameSource.RiotVerified}' ELSE '{OcrNameSource.RiotClaimed}' END
            FROM player_riot_accounts WHERE user_id = ANY(@userIds)
            UNION ALL
            SELECT ra.user_id, h.game_name, '{OcrNameSource.RiotPrevious}'
            FROM riot_account_name_history h
            JOIN riot_accounts ra ON ra.puuid = h.puuid
            WHERE ra.user_id = ANY(@userIds) AND h.seen_until > now() - make_interval(days => @days)
            """,
            new { userIds, days = (int)PreviousNameWindow.TotalDays })).ToList();

        return new OcrRosters(ToPlayers(members, riotNames, team1Id), ToPlayers(members, riotNames, team2Id));
    }

    private static async Task<List<MemberRow>> LoadMembersAsync(IDbConnection conn, Guid team1Id, Guid team2Id) =>
        (await conn.QueryAsync<MemberRow>(
            """
            SELECT tm.team_id AS CompetitorId, tm.user_id AS UserId, p.username AS Username, p.riot_tag AS RiotTag
            FROM team_members tm
            LEFT JOIN profiles p ON p.id = tm.user_id
            WHERE tm.team_id = ANY(@ids) AND tm.is_active = TRUE AND tm.role <> 'coach'
            UNION ALL
            SELECT tp.id, tp.user_id, p.username, p.riot_tag
            FROM tournament_participants tp
            LEFT JOIN profiles p ON p.id = tp.user_id
            WHERE tp.id = ANY(@ids) AND tp.participant_type = 'solo' AND tp.user_id IS NOT NULL
            LIMIT 60
            """,
            new { ids = new[] { team1Id, team2Id } })).ToList();

    private static IReadOnlyList<OcrRosterPlayer> ToPlayers(List<MemberRow> members, List<RiotNameRow> riotNames, Guid competitorId) =>
        members.Where(m => m.CompetitorId == competitorId)
            .GroupBy(m => m.UserId)
            .Select(g => new OcrRosterPlayer(g.Key.ToString(), Candidates(g.First(), riotNames.Where(r => r.UserId == g.Key))))
            .Where(p => p.Candidates.Count > 0)
            .Take(15)
            .ToList();

    /// <summary>Best source first; a name already listed under a stronger source is not repeated.</summary>
    internal static IReadOnlyList<OcrRosterName> Candidates(MemberRow member, IEnumerable<RiotNameRow> riotNames)
    {
        var all = riotNames.Select(r => new OcrRosterName(r.GameName, r.Source))
            .Append(new OcrRosterName(StripTag(member.RiotTag) ?? "", OcrNameSource.RiotClaimed))
            .Append(new OcrRosterName(member.Username ?? "", OcrNameSource.Username));
        return all.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c with { Name = c.Name.Trim() })
            .OrderBy(c => Rank(c.Source))
            .DistinctBy(c => c.Name.ToLowerInvariant())
            .Take(12)
            .ToList();
    }

    private static int Rank(string source) => source switch
    {
        OcrNameSource.RiotVerified => 0,
        OcrNameSource.RiotPrevious => 1,
        OcrNameSource.RiotClaimed => 2,
        _ => 3,
    };

    private static string? StripTag(string? riotTag) =>
        string.IsNullOrWhiteSpace(riotTag) ? null : riotTag.Split('#', 2)[0];
}
