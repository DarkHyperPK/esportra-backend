namespace Esportra.Core.Tournaments;

/// <summary>
/// Roster-building capacity checks (manage roster UI). Does not require exact starter counts;
/// use <see cref="RosterLineupValidator"/> at tournament registration.
/// </summary>
public static class RosterCapacityValidator
{
    public static void ValidateCanAddMember(
        RosterModeRules mode,
        IReadOnlyList<RosterLineupMember> current,
        string newRole)
    {
        var role = RosterLineupValidator.NormalizeRole(newRole, newRole == "starter");
        var counts = CountRoles(current);

        if (role == "coach")
        {
            if (!mode.AllowsCoaches)
                throw new InvalidOperationException("This game mode does not allow coaches.");
            if (counts.Coaches >= mode.MaxCoaches)
                throw new InvalidOperationException($"Coach limit reached (max {mode.MaxCoaches}).");
            return;
        }

        if (role == "substitute" && !mode.AllowsSubstitutes)
            throw new InvalidOperationException("This game mode does not allow substitutes.");

        var maxPlayers = mode.MaxRosterSize ?? mode.TeamSize;
        if (counts.Players >= maxPlayers)
            throw new InvalidOperationException($"Player limit reached (max {maxPlayers}).");

        if (role == "substitute")
        {
            var maxSubstitutes = ResolveMaxSubstitutes(mode);
            if (maxSubstitutes.HasValue && counts.Substitutes >= maxSubstitutes.Value)
                throw new InvalidOperationException($"Substitute limit reached (max {maxSubstitutes.Value}).");
        }
    }

    public static void ValidateRoleChange(
        RosterModeRules mode,
        IReadOnlyList<RosterLineupMember> current,
        Guid userId,
        string newRole)
    {
        var withoutUser = current.Where(m => m.UserId != userId).ToList();
        ValidateCanAddMember(mode, withoutUser, newRole);
    }

    public static RosterModeRules FromModeResolution(
        int teamSize,
        bool allowsSubstitutes,
        int? maxRosterSize,
        int? maxSubstitutes,
        bool allowsCoaches,
        int maxCoaches) =>
        new(teamSize, allowsSubstitutes, maxRosterSize, maxSubstitutes, allowsCoaches, maxCoaches);

    private static (int Players, int Substitutes, int Coaches) CountRoles(IReadOnlyList<RosterLineupMember> members)
    {
        var normalized = members
            .Select(m => RosterLineupValidator.NormalizeRole(m.RosterRole, m.IsStarter))
            .ToList();

        var substitutes = normalized.Count(r => r == "substitute");
        var coaches = normalized.Count(r => r == "coach");
        var players = normalized.Count(r => r is "starter" or "substitute");
        return (players, substitutes, coaches);
    }

    private static int? ResolveMaxSubstitutes(RosterModeRules mode) =>
        mode.MaxSubstitutes
        ?? (mode.MaxRosterSize.HasValue ? Math.Max(mode.MaxRosterSize.Value - mode.TeamSize, 0) : null);
}
