using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class BracketCompetitorResolverTests
{
    [Fact]
    public void BracketTeamResolutionSql_includes_solo_participant_join_condition()
    {
        Assert.Contains("participant_type = 'solo'", BracketTeamResolutionSql.Team1Joins);
        Assert.Contains("participant_type = 'solo'", BracketTeamResolutionSql.Team2Joins);
        Assert.Contains("sp1.username", BracketTeamResolutionSql.Team1Columns);
        Assert.Contains("'solo'", BracketTeamResolutionSql.Team1Columns);
    }

    [Theory]
    [InlineData(false, null, false, "MY-TEAM", "solo", "solo")]
    [InlineData(false, "team", false, "MY-TEAM", "solo", "solo")]
    [InlineData(false, "team", false, "MY-TEAM", "team", "team")]
    public void ResolveTeamKind_treats_participant_type_solo_as_solo_kind(
        bool isMock,
        string? teamKind,
        bool isSolo,
        string tag,
        string participantType,
        string expected)
    {
        Assert.Equal(
            expected,
            ParticipantEntryMetadata.ResolveTeamKind(isMock, teamKind, isSolo, tag, participantType));
    }

    [Theory]
    [InlineData(false, "team", "solo", "solo_player")]
    public void ResolveEntryKind_maps_native_solo_participant(
        bool isMock,
        string teamKind,
        string participantType,
        string expected)
    {
        Assert.Equal(expected, ParticipantEntryMetadata.ResolveEntryKind(isMock, teamKind, participantType));
    }
}
