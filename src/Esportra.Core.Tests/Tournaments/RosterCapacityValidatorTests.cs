using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class RosterCapacityValidatorTests
{
    private static readonly RosterModeRules ApexTriosRules = new(
        TeamSize: 3,
        AllowsSubstitutes: true,
        MaxRosterSize: 5,
        MaxSubstitutes: 2,
        AllowsCoaches: true,
        MaxCoaches: 2);

    private static readonly RosterModeRules ValorantRules = new(
        TeamSize: 5,
        AllowsSubstitutes: true,
        MaxRosterSize: 7,
        MaxSubstitutes: 2,
        AllowsCoaches: true,
        MaxCoaches: 2);

    private static readonly RosterModeRules Skirmish2v2Rules = new(
        TeamSize: 2,
        AllowsSubstitutes: true,
        MaxRosterSize: 3,
        MaxSubstitutes: 1,
        AllowsCoaches: true,
        MaxCoaches: 2);

    [Fact]
    public void ValidateCanAddMember_allows_five_players_and_two_coaches_for_apex_trios()
    {
        var current = Enumerable.Range(1, 4)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .ToList();

        RosterCapacityValidator.ValidateCanAddMember(ApexTriosRules, current, "starter");

        current.Add(new RosterLineupMember(Guid.NewGuid(), "starter", true));
        RosterCapacityValidator.ValidateCanAddMember(ApexTriosRules, current, "coach");
        current.Add(new RosterLineupMember(Guid.NewGuid(), "coach", false));
        RosterCapacityValidator.ValidateCanAddMember(ApexTriosRules, current, "coach");
    }

    [Fact]
    public void ValidateCanAddMember_rejects_sixth_player_for_apex_trios()
    {
        var current = Enumerable.Range(1, 5)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RosterCapacityValidator.ValidateCanAddMember(ApexTriosRules, current, "starter"));
        Assert.Contains("max 5", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCanAddMember_allows_seven_players_and_two_coaches_for_valorant()
    {
        var current = Enumerable.Range(1, 7)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .ToList();

        RosterCapacityValidator.ValidateCanAddMember(ValorantRules, current, "coach");
        current.Add(new RosterLineupMember(Guid.NewGuid(), "coach", false));
        RosterCapacityValidator.ValidateCanAddMember(ValorantRules, current, "coach");
    }

    [Fact]
    public void ValidateCanAddMember_caps_skirmish_2v2_at_three_players()
    {
        var current = Enumerable.Range(1, 3)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .ToList();

        Assert.Throws<InvalidOperationException>(() =>
            RosterCapacityValidator.ValidateCanAddMember(Skirmish2v2Rules, current, "starter"));
    }

    [Fact]
    public void ValidateRoleChange_allows_switching_starter_to_substitute_within_cap()
    {
        var userId = Guid.NewGuid();
        var current = new List<RosterLineupMember>
        {
            new(userId, "starter", true),
            new(Guid.NewGuid(), "starter", true),
            new(Guid.NewGuid(), "starter", true),
        };

        RosterCapacityValidator.ValidateRoleChange(ApexTriosRules, current, userId, "substitute");
    }

    [Fact]
    public void ValidateRoleChange_rejects_third_coach()
    {
        var userId = Guid.NewGuid();
        var current = new List<RosterLineupMember>
        {
            new(Guid.NewGuid(), "coach", false),
            new(Guid.NewGuid(), "coach", false),
            new(userId, "starter", true),
        };

        Assert.Throws<InvalidOperationException>(() =>
            RosterCapacityValidator.ValidateRoleChange(ApexTriosRules, current, userId, "coach"));
    }
}
