using System.Text.Json;
using Esportra.Contracts.Competitive;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

/// <summary>
/// Raw veto tokens are credentials: whoever holds a team's token can act as that team in the veto.
/// A session response may only carry the caller's own derived token (VetoMyToken).
/// </summary>
public sealed class ScrimVetoTokenLeakTests
{
    private const string Team1Secret = "team1-secret-token-aaaa";
    private const string Team2Secret = "team2-secret-token-bbbb";

    private static ScrimSessionRow Row() => new()
    {
        Id = Guid.NewGuid(),
        Team1Id = Guid.NewGuid(),
        Team2Id = Guid.NewGuid(),
        Status = "in_progress",
        VetoTeam1Token = Team1Secret,
        VetoTeam2Token = Team2Secret,
        VetoMyToken = "callers-own-token",
    };

    [Fact]
    public void SerializedSession_NeverContainsEitherTeamsRawToken()
    {
        var json = JsonSerializer.Serialize(Row());
        json.Should().NotContain(Team1Secret);
        json.Should().NotContain(Team2Secret);
    }

    [Fact]
    public void SerializedSession_StillCarriesTheCallersOwnToken()
    {
        JsonSerializer.Serialize(Row()).Should().Contain("callers-own-token");
    }

    [Fact]
    public void SerializedSession_ListOfRows_NeverLeaksTokens()
    {
        var json = JsonSerializer.Serialize(new[] { Row(), Row() });
        json.Should().NotContain(Team1Secret).And.NotContain(Team2Secret);
    }
}
