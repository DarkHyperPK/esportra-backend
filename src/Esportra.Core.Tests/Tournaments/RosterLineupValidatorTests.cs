using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class RosterLineupValidatorTests
{
    private static readonly RosterModeRules ValorantRules = new(
        TeamSize: 5,
        AllowsSubstitutes: true,
        MaxRosterSize: 7,
        MaxSubstitutes: 2,
        AllowsCoaches: true,
        MaxCoaches: 2);

    [Fact]
    public void Validate_accepts_starters_subs_and_coach()
    {
        var members = Enumerable.Range(1, 5)
            .Select(i => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .Concat(Enumerable.Range(1, 2).Select(_ => new RosterLineupMember(Guid.NewGuid(), "substitute", false)))
            .Append(new RosterLineupMember(Guid.NewGuid(), "coach", false))
            .ToList();

        RosterLineupValidator.Validate(ValorantRules, members);
    }

    [Fact]
    public void Validate_rejects_wrong_starter_count()
    {
        var members = Enumerable.Range(1, 4)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .ToList();

        Assert.Throws<InvalidOperationException>(() => RosterLineupValidator.Validate(ValorantRules, members));
    }

    [Fact]
    public void Validate_rejects_too_many_substitutes()
    {
        var members = Enumerable.Range(1, 5)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .Concat(Enumerable.Range(1, 3).Select(_ => new RosterLineupMember(Guid.NewGuid(), "substitute", false)))
            .ToList();

        Assert.Throws<InvalidOperationException>(() => RosterLineupValidator.Validate(ValorantRules, members));
    }

    [Fact]
    public void Validate_coaches_do_not_count_toward_player_cap()
    {
        var members = Enumerable.Range(1, 5)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .Concat(Enumerable.Range(1, 2).Select(_ => new RosterLineupMember(Guid.NewGuid(), "substitute", false)))
            .Append(new RosterLineupMember(Guid.NewGuid(), "coach", false))
            .Append(new RosterLineupMember(Guid.NewGuid(), "coach", false))
            .ToList();

        RosterLineupValidator.Validate(ValorantRules, members);
    }

    [Fact]
    public void Validate_rejects_coach_marked_as_starter()
    {
        var members = Enumerable.Range(1, 4)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .Append(new RosterLineupMember(Guid.NewGuid(), "starter", true, "coach"))
            .ToList();

        Assert.Throws<InvalidOperationException>(() => RosterLineupValidator.Validate(ValorantRules, members));
    }
}
