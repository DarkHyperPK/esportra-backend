using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Core.Broadcast;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task StageFollowing_AddsEligibleAdvancingMatches_WithoutDuplicates_AndStopsWithoutDeleting()
    {
        var setup = await SeedSetupMatchAsync(); var client = _factory.CreateAuthenticatedClient(setup.Owner);
        await using var conn = _seeder.OpenConnection();
        var stage = await conn.ExecuteScalarAsync<Guid>("SELECT stage_id FROM brkt_versions WHERE id = @version", new { version = setup.Version });
        var unresolved = await _seeder.SeedMatchAsync(setup.Version, matchNumber: 2);
        var played = await _seeder.SeedMatchAsync(setup.Version, matchNumber: 3, team1Id: setup.TeamA, team2Id: setup.TeamB, matchStatus: "completed");
        var path = $"/api/broadcast/shows/{setup.Show}/enrollment";
        var request = new EnrollmentRequest(true, [stage], 0);
        var saved = await client.PutAsJsonAsync(path, request);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        (await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("enabled").GetBoolean().Should().BeTrue();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_show_matches WHERE show_id = @show", new { show = setup.Show })).Should().Be(1);
        (await conn.ExecuteScalarAsync<Guid>("SELECT match_id FROM broadcast_show_matches WHERE show_id = @show", new { show = setup.Show })).Should().Be(setup.Match);
        (await client.PutAsJsonAsync(path, request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await conn.ExecuteAsync("UPDATE brkt_matches SET team1_id = @a, team2_id = @b WHERE id = @unresolved", new { a = setup.TeamA, b = setup.TeamB, unresolved });
        using var scope = _factory.Services.CreateScope(); var enrollment = scope.ServiceProvider.GetRequiredService<IBroadcastEnrollmentRepository>();
        await Task.WhenAll(enrollment.EnrollAsync(setup.Show, default), enrollment.EnrollAsync(setup.Show, default));
        var matches = (await conn.QueryAsync<Guid>("SELECT match_id FROM broadcast_show_matches WHERE show_id = @show", new { show = setup.Show })).ToArray();
        matches.Should().BeEquivalentTo([setup.Match, unresolved]); matches.Should().NotContain(played);
        (await client.PutAsJsonAsync(path, new EnrollmentRequest(false, [stage], 1))).StatusCode.Should().Be(HttpStatusCode.OK);
        await _seeder.SeedMatchAsync(setup.Version, matchNumber: 4, team1Id: setup.TeamA, team2Id: setup.TeamB);
        await enrollment.ProcessBatchAsync(default);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_show_matches WHERE show_id = @show", new { show = setup.Show })).Should().Be(2);
    }

    [Fact]
    public async Task StageFollowing_RejectsForeignOwnerStagesAndLiveEdits_AndEndedShowsCannotEnroll()
    {
        var setup = await SeedSetupMatchAsync(); var foreign = await SeedSetupMatchAsync();
        await using var conn = _seeder.OpenConnection();
        var stage = await conn.ExecuteScalarAsync<Guid>("SELECT stage_id FROM brkt_versions WHERE id = @version", new { version = setup.Version });
        var foreignStage = await conn.ExecuteScalarAsync<Guid>("SELECT stage_id FROM brkt_versions WHERE id = @version", new { version = foreign.Version });
        var path = $"/api/broadcast/shows/{setup.Show}/enrollment"; var client = _factory.CreateAuthenticatedClient(setup.Owner);
        (await _factory.CreateAuthenticatedClient(foreign.Owner).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateAuthenticatedClient(foreign.Owner).PutAsJsonAsync(path, new EnrollmentRequest(true, [stage], 0))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync(path, new EnrollmentRequest(true, [foreignStage], 0))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(path, new EnrollmentRequest(true, [], 0))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'live' WHERE id = @show", new { show = setup.Show });
        (await client.PutAsJsonAsync(path, new EnrollmentRequest(true, [stage], 0))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'ended', follow_bracket = true, followed_stage_ids = @stages WHERE id = @show", new { show = setup.Show, stages = new[] { stage } });
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IBroadcastEnrollmentRepository>().EnrollAsync(setup.Show, default)).Added.Should().Be(0);
    }
}
