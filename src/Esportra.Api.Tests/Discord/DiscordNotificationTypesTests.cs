using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Discord;

public sealed class DiscordNotificationTypesTests
{
    // ── Set membership ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("match_ready")]
    [InlineData("result_reported")]
    [InlineData("result_disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("dispute_rejected")]
    [InlineData("tournament_registered")]
    [InlineData("tournament_announcement")]
    [InlineData("tournament_invite")]
    [InlineData("result_accepted")]
    [InlineData("match_walkover")]
    [InlineData("match_schedule_changed")]
    [InlineData("match_chat_message")]
    [InlineData("checkin_open")]
    [InlineData("check_in_reminder")]
    [InlineData("party_code_submitted")]
    [InlineData("scheduling_escalation")]
    [InlineData("time_proposal_received")]
    [InlineData("time_proposal_accepted")]
    [InlineData("time_proposal_rejected")]
    [InlineData("time_proposal_countered")]
    [InlineData("team_invite")]
    [InlineData("team_invite_response")]
    [InlineData("team_captain_changed")]
    [InlineData("team_member_removed")]
    [InlineData("br_round_active")]
    [InlineData("dispute_reopened")]
    public void DmEligibleTypes_ContainsExpectedType(string type)
    {
        DiscordNotificationTypes.DmEligibleTypes.Should().Contain(type);
    }

    [Fact]
    public void DmEligibleTypes_HasExpectedCount()
    {
        DiscordNotificationTypes.DmEligibleTypes.Should().HaveCount(26);
    }

    // ── Exclusions (removed or policy-excluded types) ─────────────────────────

    [Theory]
    [InlineData("match_completed")]     // removed — dead weight
    [InlineData("veto_your_turn")]      // excluded by CEO
    [InlineData("veto_completed")]      // excluded by CEO
    [InlineData("dispute_filed")]       // excluded by policy
    [InlineData("broadcast")]           // excluded by policy
    [InlineData("staff_invite")]        // excluded by policy
    public void DmEligibleTypes_DoesNotContainExcludedType(string type)
    {
        DiscordNotificationTypes.DmEligibleTypes.Should().NotContain(type);
    }

    // ── Case-insensitive lookup ───────────────────────────────────────────────

    [Theory]
    [InlineData("MATCH_READY")]
    [InlineData("Match_Ready")]
    [InlineData("DISPUTE_RESOLVED")]
    [InlineData("CHECK_IN_REMINDER")]
    public void DmEligibleTypes_LookupIsCaseInsensitive(string upperCaseType)
    {
        DiscordNotificationTypes.DmEligibleTypes.Contains(upperCaseType).Should().BeTrue();
    }
}
