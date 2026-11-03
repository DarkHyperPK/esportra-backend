using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimListingRosterAndRecordTests(ApiFactory factory)
{
    /// <summary>A captain with four teammates; a full VALORANT roster (5 starters) and a CS2 roster one player short.</summary>
    private async Task<(Guid Captain, Guid Team)> SeedAsync()
    {
        var users = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var u in users) await factory.Seeder.SeedAuthUserAsync(u);
        var team = await factory.Seeder.SeedTeamAsync(users[0], "Roster " + Guid.NewGuid().ToString("N")[..6]);
        await using var conn = factory.Seeder.OpenConnection();
        foreach (var u in users[1..]) await conn.ExecuteAsync("INSERT INTO public.team_members (team_id, user_id, role) VALUES (@team, @u, 'member') ON CONFLICT DO NOTHING", new { team, u });
        var val = Guid.NewGuid();
        var cs = Guid.NewGuid();
        await conn.ExecuteAsync("""
            UPDATE public.teams SET scrim_history_public = TRUE WHERE id = @team;
            INSERT INTO public.team_rosters (id, team_id, name, game, team_size) VALUES (@val, @team, 'Main', 'valorant', 5), (@cs, @team, 'CS', 'cs2', 5);
            """, new { team, val, cs });
        foreach (var u in users) await conn.ExecuteAsync("INSERT INTO public.team_roster_members (roster_id, user_id, is_starter) VALUES (@val, @u, TRUE)", new { val, u });
        foreach (var u in users[..4]) await conn.ExecuteAsync("INSERT INTO public.team_roster_members (roster_id, user_id, is_starter) VALUES (@cs, @u, TRUE)", new { cs, u });
        return (users[0], team);
    }

    [Fact]
    public async Task Only_games_with_a_full_roster_are_eligible_and_only_they_can_be_listed()
    {
        var (captain, team) = await SeedAsync();
        var client = factory.CreateAuthenticatedClient(captain);

        var eligible = await client.GetFromJsonAsync<JsonElement>($"/api/scrims/teams/{team}/eligible-games");
        eligible.GetProperty("games").EnumerateArray().Select(g => g.GetString()).Should().Equal("valorant");

        (await client.PostAsJsonAsync("/api/scrims/listings", new { team_id = team, game_slug = "cs2", best_of = 1 })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/scrims/listings", new { team_id = team, game_slug = "valorant", best_of = 1 })).StatusCode.Should().Be(HttpStatusCode.Created);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await factory.CreateAuthenticatedClient(stranger).GetAsync($"/api/scrims/teams/{team}/eligible-games")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Discovery_lists_every_game_when_none_is_named_and_each_card_shows_that_games_record_only()
    {
        var (captain, team) = await SeedAsync();
        var other = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(other);
        var opponent = await factory.Seeder.SeedTeamAsync(other, "Opp " + Guid.NewGuid().ToString("N")[..6]);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status, team1_score, team2_score) VALUES (@team, @opponent, 'valorant', 'completed', 2, 0);
            INSERT INTO public.scrim_listings (team_id, game_slug, status, best_of) VALUES (@team, 'valorant', 'open', 1), (@team, 'cs2', 'open', 1);
            """, new { team, opponent });

        var client = factory.CreateAuthenticatedClient(captain);
        var all = (await client.GetFromJsonAsync<JsonElement>("/api/scrims/discover?limit=50")).EnumerateArray().Where(l => l.GetProperty("teamId").GetGuid() == team).ToList();
        all.Select(l => l.GetProperty("gameSlug").GetString()).Should().BeEquivalentTo(["valorant", "cs2"]);

        var valorant = all.Single(l => l.GetProperty("gameSlug").GetString() == "valorant");
        valorant.GetProperty("record").GetProperty("wins").GetInt32().Should().Be(1);
        valorant.GetProperty("scrimsCompleted").GetInt32().Should().Be(1);
        var cs = all.Single(l => l.GetProperty("gameSlug").GetString() == "cs2");
        cs.GetProperty("record").GetProperty("wins").GetInt32().Should().Be(0);   // the VALORANT win never shows on the CS2 card
        cs.GetProperty("scrimsCompleted").GetInt32().Should().Be(0);

        var onlyCs = (await client.GetFromJsonAsync<JsonElement>("/api/scrims/discover?game=cs2&limit=50")).EnumerateArray().Where(l => l.GetProperty("teamId").GetGuid() == team);
        onlyCs.Should().ContainSingle();
    }
}
