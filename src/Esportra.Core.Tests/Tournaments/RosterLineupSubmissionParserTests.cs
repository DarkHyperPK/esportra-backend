using System.Text.Json;
using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class RosterLineupSubmissionParserTests
{
    [Fact]
    public void Parse_reads_camelCase_lineup_entries()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var json = $$"""
        {
          "starters": [
            { "userId": "{{userA}}", "displayName": "Alpha" },
            { "userId": "{{userB}}", "displayName": "Beta" }
          ],
          "substitutes": [
            { "userId": "{{Guid.NewGuid()}}", "displayName": "Sub" }
          ],
          "coaches": []
        }
        """;

        var parsed = RosterLineupSubmissionParser.Parse(json);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(2, parsed.Count(entry => entry.Role == "starter"));
        Assert.Contains(parsed, entry => entry.UserId == userA && entry.DisplayName == "Alpha");
    }

    [Fact]
    public void Parse_rejects_unknown_user_shape()
    {
        const string json = """{ "starters": [{ "displayName": "Missing user" }] }""";
        Assert.Throws<InvalidOperationException>(() => RosterLineupSubmissionParser.Parse(json));
    }

    [Fact]
    public void Parse_unwraps_double_encoded_json_string()
    {
        var userId = Guid.NewGuid();
        var inner = $$"""{"starters":[{"userId":"{{userId}}","displayName":"Alpha"}],"substitutes":[],"coaches":[]}""";
        var json = JsonSerializer.Serialize(inner);

        var parsed = RosterLineupSubmissionParser.Parse(json);
        Assert.Single(parsed);
        Assert.Equal(userId, parsed[0].UserId);
    }
}

public sealed class SkirmishRosterLineupValidatorTests
{
    private static readonly RosterModeRules Skirmish2v2Rules = new(
        TeamSize: 2,
        AllowsSubstitutes: true,
        MaxRosterSize: 3,
        MaxSubstitutes: 1,
        AllowsCoaches: true,
        MaxCoaches: 2);

    [Fact]
    public void Validate_accepts_two_starters_and_one_substitute()
    {
        var members = new List<RosterLineupMember>
        {
            new(Guid.NewGuid(), "starter", true),
            new(Guid.NewGuid(), "starter", true),
            new(Guid.NewGuid(), "substitute", false),
        };

        RosterLineupValidator.Validate(Skirmish2v2Rules, members);
    }

    [Fact]
    public void Validate_rejects_five_starters_from_full_roster_pool()
    {
        var members = Enumerable.Range(0, 5)
            .Select(_ => new RosterLineupMember(Guid.NewGuid(), "starter", true))
            .ToList();

        Assert.Throws<InvalidOperationException>(() => RosterLineupValidator.Validate(Skirmish2v2Rules, members));
    }
}
