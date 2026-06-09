namespace Esportra.Core.Tournaments;

public static class RosterLineupSnapshotHelper
{
    public sealed record Row(
        Guid UserId,
        string? Username,
        string? FullName,
        string? RosterRole,
        bool IsStarter);

    public sealed record Entry(string UserId, string DisplayName);

    public sealed record Lineup(
        IReadOnlyList<Entry> Starters,
        IReadOnlyList<Entry> Substitutes,
        IReadOnlyList<Entry> Coaches);

    public static Lineup Build(IEnumerable<Row> rows)
    {
        var grouped = rows
            .Select(row =>
            {
                var role = RosterLineupValidator.NormalizeRole(row.RosterRole, row.IsStarter);
                return new
                {
                    row.UserId,
                    DisplayName = ResolveDisplayName(row),
                    Role = role,
                };
            })
            .GroupBy(x => x.UserId)
            .Select(g => g.First())
            .ToList();

        List<Entry> Pick(string role) =>
            grouped
                .Where(x => x.Role == role)
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new Entry(x.UserId.ToString(), x.DisplayName))
                .ToList();

        return new Lineup(Pick("starter"), Pick("substitute"), Pick("coach"));
    }

    public static IReadOnlyList<string> ToDisplayNameList(Lineup lineup) =>
        lineup.Starters
            .Select(x => x.DisplayName)
            .Concat(lineup.Substitutes.Select(x => x.DisplayName))
            .Concat(lineup.Coaches.Select(x => x.DisplayName))
            .ToList();

    private static string ResolveDisplayName(Row row) =>
        !string.IsNullOrWhiteSpace(row.Username) ? row.Username.Trim()
        : !string.IsNullOrWhiteSpace(row.FullName) ? row.FullName.Trim()
        : row.UserId.ToString();
}
