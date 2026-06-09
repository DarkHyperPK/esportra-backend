using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class ParticipantEntryMetadataTests
{
    [Theory]
    [InlineData(true, "team", false, "mock-abc", "mock")]
    [InlineData(false, "solo", true, "solo-abc", "solo")]
    [InlineData(false, null, false, "mock-abc", "mock")]
    [InlineData(false, null, false, "MY-TEAM", "team")]
    public void ResolveTeamKind_uses_explicit_and_fallback_signals(
        bool isMock,
        string? teamKind,
        bool isSolo,
        string tag,
        string expected,
        string? participantType = null)
    {
        Assert.Equal(expected, ParticipantEntryMetadata.ResolveTeamKind(isMock, teamKind, isSolo, tag, participantType));
    }

    [Theory]
    [InlineData(false, "team", "team", "real_team")]
    [InlineData(false, "solo", "solo", "solo_player")]
    [InlineData(true, "team", "team", "mock")]
    public void ResolveEntryKind_maps_display_entry_kind(
        bool isMock,
        string teamKind,
        string participantType,
        string expected)
    {
        Assert.Equal(expected, ParticipantEntryMetadata.ResolveEntryKind(isMock, teamKind, participantType));
    }
}
