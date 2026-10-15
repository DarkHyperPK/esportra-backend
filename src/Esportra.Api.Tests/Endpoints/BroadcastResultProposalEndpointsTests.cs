using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    private async Task<(Guid Owner, Guid Show, Guid Match, Guid Node, HttpClient Desktop, ResultProposalRequest Request)> SeedResultProposalAsync()
    {
        var setup = await SeedSetupMatchAsync();
        var owner = _factory.CreateAuthenticatedClient(setup.Owner);
        (await owner.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = setup.Match, matchCode = "FINAL" })).EnsureSuccessStatusCode();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE brkt_matches SET best_of = 3 WHERE id = @match", new { match = setup.Match });
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var node = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) SELECT org_id, 'Synthetic result PC', @hash FROM broadcast_shows WHERE id = @show RETURNING id", new { hash, show = setup.Show });
        var desktop = _factory.CreateClient(); desktop.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return (setup.Owner, setup.Show, setup.Match, node, desktop,
            new ResultProposalRequest(Guid.NewGuid(), node, setup.Match, "FINAL", setup.TeamA, setup.TeamB, 3, 1, 13, 7, "Operator checked scoreboard"));
    }

    [Fact]
    public async Task ResultProposal_NodeCannotWriteOfficialResult_OwnerNeedsActiveResultPermission_AndReviewIsIdempotent()
    {
        var s = await SeedResultProposalAsync(); var owner = _factory.CreateAuthenticatedClient(s.Owner);
        var path = $"/api/broadcast/nodes/shows/{s.Show}/result-proposals";
        var received = await s.Desktop.PostAsJsonAsync(path, s.Request);
        received.StatusCode.Should().Be(HttpStatusCode.OK, await received.Content.ReadAsStringAsync());
        var id = (await received.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("proposalId").GetGuid();
        (await s.Desktop.PostAsJsonAsync(path, s.Request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await s.Desktop.PostAsJsonAsync(path, s.Request with { TeamAScore = 14 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var conn = _seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM brkt_match_games WHERE match_id = @match", new { match = s.Match })).Should().Be(0);
        var review = $"/api/broadcast/shows/{s.Show}/result-proposals/{id}/review";
        (await owner.PostAsJsonAsync(review, new ResultReviewRequest("apply", null, null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await conn.ExecuteAsync("INSERT INTO broadcast_crew (show_id, user_id, role, is_active, can_submit_result) VALUES (@show, @owner, 'producer', true, true)", new { show = s.Show, owner = s.Owner });
        var applied = await owner.PostAsJsonAsync(review, new ResultReviewRequest("apply", null, null));
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        (await conn.ExecuteScalarAsync<int>("SELECT team1_score FROM brkt_matches WHERE id = @match", new { match = s.Match })).Should().Be(1);
        (await owner.PostAsJsonAsync(review, new ResultReviewRequest("apply", null, null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(review, new ResultReviewRequest("reject", null, null))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_events WHERE show_id = @show AND event_type = 'MatchResultReviewed'", new { show = s.Show })).Should().Be(1);
        var listing = await owner.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{s.Show}/result-proposals");
        listing.GetProperty("canReview").GetBoolean().Should().BeTrue();
        listing.GetProperty("proposals")[0].GetProperty("status").GetString().Should().Be("applied");
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_corrections WHERE show_id = @show AND correction_type = 'air_result_review'", new { show = s.Show })).Should().Be(1);
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'ended' WHERE id = @show", new { show = s.Show });
        var report = await owner.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{s.Show}/report");
        report.GetProperty("matches")[0].GetProperty("maps")[0].GetProperty("source").GetString().Should().Be("manual");
    }

    [Fact]
    public async Task ResultProposal_BindsOriginalPairingParticipantsShowAndMap_RejectsForeignOrRevokedNode()
    {
        var s = await SeedResultProposalAsync(); var path = $"/api/broadcast/nodes/shows/{s.Show}/result-proposals";
        (await s.Desktop.PostAsJsonAsync(path, s.Request with { NodeId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await s.Desktop.PostAsJsonAsync(path, s.Request with { Team1Id = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await s.Desktop.PostAsJsonAsync(path, s.Request with { MatchCode = "OTHER" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await s.Desktop.PostAsJsonAsync(path, s.Request with { MapNumber = 4 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await s.Desktop.PostAsJsonAsync(path, s.Request with { TeamBScore = 13 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (foreignOwner, foreignOrg) = await SeedOrgOwnerAsync(); var foreignShow = await SeedShowAsync(foreignOrg, foreignOwner);
        (await s.Desktop.PostAsJsonAsync($"/api/broadcast/nodes/shows/{foreignShow}/result-proposals", s.Request)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.CreateAuthenticatedClient(foreignOwner).GetAsync($"/api/broadcast/shows/{s.Show}/result-proposals")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @foreignOrg WHERE id = (SELECT tournament_id FROM broadcast_shows WHERE id = @show)", new { foreignOrg, show = s.Show });
        (await s.Desktop.PostAsJsonAsync(path, s.Request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await conn.ExecuteAsync("UPDATE broadcast_nodes SET is_active = false WHERE id = @node", new { node = s.Node });
        (await s.Desktop.PostAsJsonAsync(path, s.Request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResultProposal_StaleOfficialScoreAndFinalizedMatchCannotBeOverwritten_RejectionLeavesResultsUntouched()
    {
        var s = await SeedResultProposalAsync(); var owner = _factory.CreateAuthenticatedClient(s.Owner);
        var received = await s.Desktop.PostAsJsonAsync($"/api/broadcast/nodes/shows/{s.Show}/result-proposals", s.Request);
        received.EnsureSuccessStatusCode(); var id = (await received.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("proposalId").GetGuid();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO broadcast_crew (show_id, user_id, role, is_active, can_submit_result) VALUES (@show, @owner, 'producer', true, true); INSERT INTO brkt_match_games (match_id, game_number, team1_score, team2_score, status) VALUES (@match, 1, 9, 13, 'completed')", new { show = s.Show, owner = s.Owner, match = s.Match });
        var review = $"/api/broadcast/shows/{s.Show}/result-proposals/{id}/review";
        (await owner.PostAsJsonAsync(review, new ResultReviewRequest("apply", null, null))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conn.ExecuteScalarAsync<int>("SELECT team1_score FROM brkt_match_games WHERE match_id = @match", new { match = s.Match })).Should().Be(9);
        await conn.ExecuteAsync("UPDATE brkt_matches SET status = 'completed' WHERE id = @match", new { match = s.Match });
        (await owner.PostAsJsonAsync(review, new ResultReviewRequest("apply", 9, 13))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.PostAsJsonAsync(review, new ResultReviewRequest("reject", 9, 13))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await conn.ExecuteScalarAsync<int>("SELECT team1_score FROM brkt_match_games WHERE match_id = @match", new { match = s.Match })).Should().Be(9);
    }
}
