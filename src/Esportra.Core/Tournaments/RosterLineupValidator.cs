namespace Esportra.Core.Tournaments;

public sealed record RosterLineupMember(
    Guid UserId,
    string RosterRole,
    bool IsStarter,
    string? TeamMemberRole = null);

public sealed record RosterModeRules(
    int TeamSize,
    bool AllowsSubstitutes,
    int? MaxRosterSize,
    int? MaxSubstitutes,
    bool AllowsCoaches,
    int MaxCoaches);

public static class RosterLineupValidator
{
    public static void Validate(RosterModeRules mode, IReadOnlyList<RosterLineupMember> members)
    {
        var normalized = members
            .Select(m => new RosterLineupMember(
                m.UserId,
                NormalizeRole(m.RosterRole, m.IsStarter),
                NormalizeRole(m.RosterRole, m.IsStarter) == "starter",
                m.TeamMemberRole))
            .ToList();

        var starters = normalized.Where(m => m.RosterRole == "starter").Select(m => m.UserId).Distinct().ToList();
        var substitutes = normalized.Where(m => m.RosterRole == "substitute").Select(m => m.UserId).Distinct().ToList();
        var coaches = normalized.Where(m => m.RosterRole == "coach").Select(m => m.UserId).Distinct().ToList();
        var players = starters.Concat(substitutes).Distinct().ToList();

        if (starters.Count != mode.TeamSize)
            throw new InvalidOperationException(
                $"Roster must have exactly {mode.TeamSize} starter(s). It currently has {starters.Count}.");

        if (!mode.AllowsSubstitutes && substitutes.Count > 0)
            throw new InvalidOperationException("This tournament mode does not allow substitutes.");

        var maxSubstitutes = mode.MaxSubstitutes
            ?? (mode.MaxRosterSize.HasValue ? Math.Max(mode.MaxRosterSize.Value - mode.TeamSize, 0) : (int?)null);

        if (maxSubstitutes.HasValue && substitutes.Count > maxSubstitutes.Value)
            throw new InvalidOperationException(
                $"Roster has {substitutes.Count} substitute(s), exceeding the limit of {maxSubstitutes.Value}.");

        if (mode.MaxRosterSize.HasValue && players.Count > mode.MaxRosterSize.Value)
            throw new InvalidOperationException(
                $"Roster has {players.Count} players, exceeding the {mode.MaxRosterSize.Value}-player limit.");

        if (!mode.AllowsCoaches && coaches.Count > 0)
            throw new InvalidOperationException("This tournament mode does not allow coaches.");

        if (coaches.Count > mode.MaxCoaches)
            throw new InvalidOperationException(
                $"Roster has {coaches.Count} coach(es), exceeding the limit of {mode.MaxCoaches}.");

        foreach (var member in normalized.Where(m => m.RosterRole is "starter" or "substitute"))
        {
            if (string.Equals(member.TeamMemberRole, "coach", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A team coach cannot be listed as a starter or substitute on the roster.");
        }
    }

    public static string NormalizeRole(string? rosterRole, bool isStarter)
    {
        if (rosterRole is "starter" or "substitute" or "coach")
            return rosterRole;
        return isStarter ? "starter" : "substitute";
    }

    public static bool IsStarterRole(string? rosterRole, bool isStarter) =>
        NormalizeRole(rosterRole, isStarter) == "starter";
}
