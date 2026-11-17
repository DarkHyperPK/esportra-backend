using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class AirBroadcastEndpointsTests(ApiFactory factory)
{
    private async Task<(Guid User, Guid Match, Guid Tournament, Guid Device, HttpClient Client)> Seed(string game = "valorant", string status = "pending")
    {
        var user = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(user);
        var client = factory.CreateAuthenticatedClient(user);
        (await client.PostAsJsonAsync("/api/air/session", new { })).EnsureSuccessStatusCode();
        var device = Guid.NewGuid();
        (await client.PostAsJsonAsync("/api/air/devices", new { deviceId = device, name = "Production PC", mode = "production" })).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-Air-Device-Id", device.ToString());
        var tournament = await factory.Seeder.SeedTournamentAsync(user);
        var stage = await factory.Seeder.SeedStageAsync(tournament);
        var version = await factory.Seeder.SeedBracketVersionAsync(tournament, stage, "active");
        var a = await factory.Seeder.SeedTeamAsync(user, "North");
        var b = await factory.Seeder.SeedTeamAsync(user, "South");
        var match = await factory.Seeder.SeedMatchAsync(version, team1Id: a, team2Id: b, matchStatus: status);
        using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET game=@game WHERE id=@tournament", new { game, tournament });
        return (user, match, tournament, device, client);
    }
    [Fact]
    public async Task Owner_prepares_without_organization_or_web_show_and_sources_are_automatic()
    {
        var s = await Seed();
        var list = await s.Client.GetFromJsonAsync<JsonElement>("/api/air/broadcast/matches");
        list.GetProperty("matches").EnumerateArray().Should().Contain(m => m.GetProperty("id").GetGuid() == s.Match);
        var response = await s.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{s.Match}/prepare", new { requestId = Guid.NewGuid() });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var packet = await response.Content.ReadFromJsonAsync<JsonElement>();
        var raw = packet.GetProperty("configurationJson").GetString()!;
        packet.GetProperty("checksum").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant());
        using var config = JsonDocument.Parse(raw);
        config.RootElement.GetProperty("rundown").GetArrayLength().Should().Be(5);
        config.RootElement.GetProperty("rundown").EnumerateArray().Count(i => i.GetProperty("overlay").GetString() == "ingame").Should().Be(1);
        config.RootElement.GetProperty("teams")[0].GetProperty("name").GetString().Should().Be("North");
        using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM broadcast_shows WHERE tournament_id=@id", new { id = s.Tournament })).Should().Be(0);
        (await conn.QuerySingleAsync<Guid?>("SELECT organization_id FROM tournaments WHERE id=@id", new { id = s.Tournament })).Should().BeNull();
    }
    [Fact]
    public async Task Outsider_cannot_discover_prepare_or_read_another_accounts_match()
    {
        var owner = await Seed(); var outsider = await Seed();
        var list = await outsider.Client.GetFromJsonAsync<JsonElement>("/api/air/broadcast/matches");
        list.GetProperty("matches").EnumerateArray().Should().NotContain(m => m.GetProperty("id").GetGuid() == owner.Match);
        (await outsider.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{owner.Match}/prepare", new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var packet = await (await owner.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{owner.Match}/prepare", new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        (await outsider.Client.GetAsync($"/api/air/broadcast/packages/{packet.GetProperty("id").GetGuid()}/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Unsupported_game_is_visible_but_never_receives_valorant_and_played_match_is_excluded()
    {
        var unsupported = await Seed("cs2"); var played = await Seed(status: "completed");
        var list = await unsupported.Client.GetFromJsonAsync<JsonElement>("/api/air/broadcast/matches");
        list.GetProperty("matches")[0].GetProperty("supported").GetBoolean().Should().BeFalse();
        (await unsupported.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{unsupported.Match}/prepare", new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var empty = await played.Client.GetFromJsonAsync<JsonElement>("/api/air/broadcast/matches"); empty.GetProperty("matches").GetArrayLength().Should().Be(0);
        (await played.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{played.Match}/prepare", new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Preparation_is_idempotent_and_published_versions_cannot_be_overwritten()
    {
        var s = await Seed(); var requestId = Guid.NewGuid(); var route = $"/api/air/broadcast/matches/{s.Match}/prepare";
        var first = await (await s.Client.PostAsJsonAsync(route, new { requestId })).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await s.Client.PostAsJsonAsync(route, new { requestId })).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("version").GetInt32().Should().Be(1);
        var unchanged = await (await s.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        unchanged.GetProperty("version").GetInt32().Should().Be(1, "fetch timestamps must not change immutable content");
        using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE brkt_matches SET scheduled_time=now() WHERE id=@id", new { id = s.Match });
        var next = await (await s.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        next.GetProperty("version").GetInt32().Should().Be(2);
        var id = first.GetProperty("id").GetGuid();
        var old = await s.Client.GetFromJsonAsync<JsonElement>($"/api/air/broadcast/packages/{id}/versions/1");
        old.GetProperty("configurationJson").GetString().Should().Be(first.GetProperty("configurationJson").GetString());
        var change = async () => await conn.ExecuteAsync("UPDATE air_match_package_versions SET configuration='{}' WHERE package_id=@id", new { id });
        await change.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Production_drafts_are_revisioned_private_and_embedded_in_new_versions()
    {
        var owner = await Seed(); var outsider = await Seed();
        var route = $"/api/air/broadcast/matches/{owner.Match}/draft";
        (await outsider.Client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var document = new { sponsors = new[] { new { name = "Match sponsor", logoUrl = "https://example.com/logo.png" } }, talent = new[] { new { name = "Presenter", role = "Host" } } };
        (await owner.Client.PutAsJsonAsync(route, new { revision = 0, document })).EnsureSuccessStatusCode();
        (await owner.Client.PutAsJsonAsync(route, new { revision = 0, document })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var packet = await (await owner.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{owner.Match}/prepare", new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        using var config = JsonDocument.Parse(packet.GetProperty("configurationJson").GetString()!);
        config.RootElement.GetProperty("sponsors")[0].GetProperty("name").GetString().Should().Be("Match sponsor");
        config.RootElement.GetProperty("casters")[0].GetProperty("displayName").GetString().Should().Be("Presenter");
        config.RootElement.GetProperty("sceneAutomationEnabled").GetBoolean().Should().BeFalse();
        config.RootElement.GetProperty("bracketSnapshot").GetProperty("matches").GetArrayLength().Should().BeGreaterThan(0);
        (await outsider.Client.GetAsync($"/api/air/broadcast/matches/{owner.Match}/bracket")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Air_invitations_require_recipient_acceptance_and_revocation_removes_match_access()
    {
        var owner = await Seed(); var crew = await Seed();
        var email = $"air-{crew.User:N}@example.invalid";
        using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE auth.users SET email=@email,email_confirmed_at=now() WHERE id=@id", new { email, id = crew.User });
        var invitation = await owner.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{owner.Match}/crew/invitations", new { email, role = "producer", canReviewResults = true });
        invitation.EnsureSuccessStatusCode(); var id = (await invitation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.Client.PostAsJsonAsync($"/api/air/broadcast/invitations/{id}/accept", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var route = $"/api/air/broadcast/matches/{owner.Match}/prepare";
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await crew.Client.PostAsJsonAsync($"/api/air/broadcast/invitations/{id}/accept", new { })).EnsureSuccessStatusCode();
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        (await owner.Client.DeleteAsync($"/api/air/broadcast/matches/{owner.Match}/crew/{id}")).EnsureSuccessStatusCode();
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_authorized_review_applies_a_proposal_and_review_is_not_replayed()
    {
        var owner = await Seed(); var outsider = await Seed();
        var packet = await (await owner.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{owner.Match}/prepare", new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        var package = packet.GetProperty("id").GetGuid(); var proposal = Guid.NewGuid();
        var route = $"/api/air/broadcast/packages/{package}/result-proposals";
        var input = new { clientKey = proposal, mapNumber = 1, teamAScore = 13, teamBScore = 5, reason = "Confirmed map" };
        (await outsider.Client.PostAsJsonAsync(route, input)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.Client.PostAsJsonAsync(route, input)).EnsureSuccessStatusCode();
        using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM brkt_match_games WHERE match_id=@id", new { id = owner.Match })).Should().Be(0);
        var review = route + $"/{proposal}/review";
        (await outsider.Client.PostAsJsonAsync(review, new { decision = "apply" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.Client.PostAsJsonAsync(review, new { decision = "apply" })).EnsureSuccessStatusCode();
        (await owner.Client.PostAsJsonAsync(review, new { decision = "apply" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conn.ExecuteScalarAsync<int>("SELECT team1_score FROM brkt_match_games WHERE match_id=@id AND game_number=1", new { id = owner.Match })).Should().Be(13);
    }

    [Fact]
    public async Task Metadata_upload_is_idempotent_private_and_cannot_spoof_official_reviews()
    {
        var owner = await Seed(); var outsider = await Seed();
        var packet = await (await owner.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{owner.Match}/prepare", new { requestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        var id = packet.GetProperty("id").GetGuid(); var eventId = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var route = $"/api/air/broadcast/packages/{id}/events";
        var batch = new { events = new[] { new { id = eventId, version = 1, at, type = "scene_request", data = new { scene = "Opening", rawPacket = "must not be stored" } } } };
        (await owner.Client.PostAsJsonAsync(route, batch)).EnsureSuccessStatusCode();
        (await owner.Client.PostAsJsonAsync(route, batch)).EnsureSuccessStatusCode();
        (await outsider.Client.PostAsJsonAsync(route, batch)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var spoof = new { events = new[] { new { id = Guid.NewGuid(), version = 1, at, type = "official_result_reviewed", data = new { decision = "apply" } } } };
        (await owner.Client.PostAsJsonAsync(route, spoof)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM air_package_events WHERE id=@eventId", new { eventId })).Should().Be(1);
        (await conn.ExecuteScalarAsync<string>("SELECT metadata::text FROM air_package_events WHERE id=@eventId", new { eventId })).Should().NotContain("rawPacket");
    }
    [Fact]
    public async Task Crew_requires_accepted_production_role_and_explicit_match_assignment()
    {
        var owner = await Seed(); var crew = await Seed();
        var org = Guid.NewGuid(); var show = Guid.NewGuid();
        using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO organizations(id,owner_id,name,slug) VALUES(@org,@owner,'Air authorization fixture',@slug);
            UPDATE tournaments SET organization_id=@org WHERE id=@tournament;
            INSERT INTO broadcast_shows(id,org_id,tournament_id,title,created_by) VALUES(@show,@org,@tournament,'Assigned match',@owner);
            INSERT INTO broadcast_show_matches(show_id,match_id) VALUES(@show,@match);
            INSERT INTO broadcast_crew(show_id,user_id,role,joined_at) VALUES(@show,@crew,'producer',now());
            """, new { org, show, owner = owner.User, tournament = owner.Tournament, match = owner.Match, crew = crew.User, slug = "air-" + org.ToString("N") });
        var route = $"/api/air/broadcast/matches/{owner.Match}/prepare";
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.OK);
        await conn.ExecuteAsync("UPDATE broadcast_crew SET joined_at=NULL WHERE show_id=@show", new { show });
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await conn.ExecuteAsync("UPDATE broadcast_crew SET joined_at=now(),role='observer' WHERE show_id=@show", new { show });
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await conn.ExecuteAsync("UPDATE broadcast_crew SET role='technical_director' WHERE show_id=@show; DELETE FROM broadcast_show_matches WHERE show_id=@show", new { show });
        (await crew.Client.PostAsJsonAsync(route, new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task Missing_or_revoked_device_and_expired_account_cannot_prepare()
    {
        var s = await Seed(); s.Client.DefaultRequestHeaders.Remove("X-Air-Device-Id");
        (await s.Client.GetAsync("/api/air/broadcast/matches")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        s.Client.DefaultRequestHeaders.Add("X-Air-Device-Id", s.Device.ToString());
        using var conn = factory.Seeder.OpenConnection(); await conn.ExecuteAsync("UPDATE air_devices SET status='revoked' WHERE id=@id", new { id = s.Device });
        (await s.Client.GetAsync("/api/air/broadcast/matches")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await conn.ExecuteAsync("UPDATE air_devices SET status='active' WHERE id=@id; UPDATE air_accounts SET valid_until=now()-interval '1 minute' WHERE user_id=@user", new { id = s.Device, user = s.User });
        (await s.Client.PostAsJsonAsync($"/api/air/broadcast/matches/{s.Match}/prepare", new { requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync("/api/air/broadcast/matches")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
