using Esportra.Contracts.Auth;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.DeveloperApi;

public sealed class ApiKeyScopesTests
{
    [Theory]
    [InlineData("tournaments:read", true)]
    [InlineData("TOURNAMENTS:READ", true)]
    [InlineData("Tournaments:Read", true)]
    [InlineData("tournaments:write", false)]
    [InlineData("", false)]
    public void HasScope_MatchesGrantedScopes_CaseInsensitive(string required, bool expected)
    {
        var granted = new[] { "tournaments:read", "brackets:read" };
        ApiKeyScopes.HasScope(granted, required).Should().Be(expected);
    }

    [Fact]
    public void HasScope_EmptyGrantedScopes_ReturnsFalse()
    {
        ApiKeyScopes.HasScope([], ApiKeyScopes.TournamentsRead).Should().BeFalse();
    }

    [Fact]
    public void HasScope_AllScopes_AllConstantsAreNonEmpty()
    {
        var scopes = new[]
        {
            ApiKeyScopes.TournamentsRead,
            ApiKeyScopes.TournamentsWrite,
            ApiKeyScopes.BracketsRead,
            ApiKeyScopes.BracketsWrite,
            ApiKeyScopes.MatchesRead,
            ApiKeyScopes.MatchesWrite,
            ApiKeyScopes.VetoRead,
            ApiKeyScopes.VetoWrite,
        };

        foreach (var scope in scopes)
            scope.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HasScope_GrantedContainsRequired_ReturnsTrue()
    {
        var all = new[]
        {
            ApiKeyScopes.TournamentsRead, ApiKeyScopes.TournamentsWrite,
            ApiKeyScopes.BracketsRead, ApiKeyScopes.BracketsWrite,
            ApiKeyScopes.MatchesRead, ApiKeyScopes.MatchesWrite,
            ApiKeyScopes.VetoRead, ApiKeyScopes.VetoWrite,
        };
        foreach (var scope in all)
            ApiKeyScopes.HasScope(all, scope).Should().BeTrue(because: $"scope {scope} is in granted list");
    }
}
