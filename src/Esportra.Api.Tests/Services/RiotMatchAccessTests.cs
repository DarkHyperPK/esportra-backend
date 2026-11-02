using System.Text.Json;
using Esportra.Api.Services;
using Esportra.Infrastructure.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Esportra.Api.Tests.Services;

public sealed class RiotMatchAccessTests
{
    [Theory]
    [InlineData("3f2a9c1e-1b7d-4e55-8a0c-9d6f2b7c1a44", true)]
    [InlineData("abcdefgh", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("short", false)]
    [InlineData("../../riot/account/v1/accounts/me", false)]
    [InlineData("abc/def/ghi/jkl", false)]
    [InlineData("abcdefgh?x=1", false)]
    public void MatchId_Format_IsStrict(string? id, bool expected) =>
        RiotMatchAccess.IsWellFormedMatchId(id).Should().Be(expected);

    private static JsonElement Match(params string[] puuids)
    {
        var players = string.Join(",", puuids.Select(p => $$"""{"puuid":"{{p}}"}"""));
        return JsonDocument.Parse($$"""{"players":[{{players}}]}""").RootElement;
    }

    [Fact]
    public void CallerPlayed_TrueOnlyWhenALinkedAccountIsInTheMatch()
    {
        RiotMatchAccess.CallerPlayed(Match("a", "b"), ["b"]).Should().BeTrue();
        RiotMatchAccess.CallerPlayed(Match("a", "b"), ["z"]).Should().BeFalse();
        RiotMatchAccess.CallerPlayed(Match("a"), []).Should().BeFalse();
        RiotMatchAccess.CallerPlayed(JsonDocument.Parse("{}").RootElement, ["a"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("/val/match/v1/matches/../../riot/account/v1/accounts/by-riot-id/x/y")]
    [InlineData("/val/match/v1/matches/%2e%2e/secret")]
    [InlineData("/val/match/v1/matches/a%2Fb")]
    [InlineData("/val/match/v1/matches/a\\b")]
    public async Task Proxy_RejectsTraversalBeforeCallingRiot(string endpoint)
    {
        var client = new RiotApiClient(new HttpClient(new ThrowingHandler()), new ConfigurationBuilder().Build());
        var (status, _) = await client.ProxyAsync("na", endpoint);
        status.Should().Be(403);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Riot must not be called for a rejected endpoint.");
    }
}
