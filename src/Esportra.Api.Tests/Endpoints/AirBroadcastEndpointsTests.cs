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
    private static async Task<Guid> MapId(NpgsqlConnection conn, string name)
    {
        var id = await conn.ExecuteScalarAsync<Guid?>("SELECT id FROM game_maps WHERE game='valorant' AND map_name=@name LIMIT 1", new { name });
        return id ?? await conn.QuerySingleAsync<Guid>("INSERT INTO game_maps (game, map_name) VALUES ('valorant', @name) RETURNING id", new { name });
    }

    [Fact]
    public async Task GetVeto_ReturnsStepsAsTheTeamsMakeThem_ForTheMatchOwnerOnly()
    {
        var s = await Seed();
        await using var conn = factory.Seeder.OpenConnection();
        var teams = await conn.QuerySingleAsync<(Guid A, Guid B)>("SELECT team1_id, team2_id FROM brkt_matches WHERE id=@id", new { id = s.Match });
        var veto = await conn.QuerySingleAsync<Guid>("INSERT INTO match_map_vetos (match_id, tournament_id, team1_id, team2_id, status) VALUES (@match, @tournament, @a, @b, 'in_progress') RETURNING id", new { match = s.Match, tournament = s.Tournament, a = teams.A, b = teams.B });
        var bind = await MapId(conn, "Bind");
        var lotus = await MapId(conn, "Lotus");
        await conn.ExecuteAsync("INSERT INTO tournament_map_pools (tournament_id, map_id) VALUES (@t, @bind), (@t, @lotus)", new { t = s.Tournament, bind, lotus });
        await conn.ExecuteAsync("""
            INSERT INTO match_map_veto_actions (veto_id, match_id, team_id, action_type, map_id, action_number, team_side, side) VALUES
              (@veto, @match, @a, 'ban', @bind, 1, 'team1', NULL),
              (@veto, @match, @b, 'pick', @lotus, 2, 'team2', NULL),
              (@veto, @match, @a, 'side', @lotus, 3, 'team1', 'defense')
            """, new { veto, match = s.Match, a = teams.A, b = teams.B, bind, lotus });

        var body = await s.Client.GetFromJsonAsync<JsonElement>($"/api/air/broadcast/matches/{s.Match}/veto");

        body.GetProperty("status").GetString().Should().Be("in_progress");
        var steps = body.GetProperty("steps").EnumerateArray().Select(x => $"{x.GetProperty("team").GetInt32()}:{x.GetProperty("action").GetString()}:{x.GetProperty("map").GetString()}:{(x.GetProperty("side").ValueKind == JsonValueKind.Null ? "" : x.GetProperty("side").GetString())}").ToArray();
        steps.Should().Equal("0:ban:Bind:", "1:pick:Lotus:DEF");
        body.GetProperty("mapPool").EnumerateArray().Select(x => x.GetString()).Should().Contain(["Bind", "Lotus"]);

        var outsider = await Seed();
        (await outsider.Client.GetAsync($"/api/air/broadcast/matches/{s.Match}/veto")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var noDevice = factory.CreateAuthenticatedClient(s.User);
        (await noDevice.GetAsync($"/api/air/broadcast/matches/{s.Match}/veto")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetVeto_FollowsTheRealVeto_CustomOrderTurnTossDeciderAndReset()
    {
        var s = await Seed();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE brkt_matches SET best_of=3 WHERE id=@id", new { id = s.Match });
        var teams = await conn.QuerySingleAsync<(Guid A, Guid B)>("SELECT team1_id, team2_id FROM brkt_matches WHERE id=@id", new { id = s.Match });
        // A custom order where team 2 bans first.
        var custom = JsonSerializer.Serialize(new object[]
        {
            new { action_number = 1, action = "ban", team = "T2", is_decider = false }, new { action_number = 2, action = "ban", team = "T1", is_decider = false },
            new { action_number = 3, action = "pick", team = "T2", is_decider = false }, new { action_number = 4, action = "pick_side", team = "T1", is_decider = false },
            new { action_number = 5, action = "pick", team = "T1", is_decider = false }, new { action_number = 6, action = "pick_side", team = "T2", is_decider = false },
            new { action_number = 7, action = "ban", team = "T2", is_decider = false }, new { action_number = 8, action = "ban", team = "T1", is_decider = false },
            new { action_number = 9, action = "pick_side", team = "T2", is_decider = true },
        });
        await conn.ExecuteAsync("INSERT INTO match_veto_settings (match_id, mode, sequence) VALUES (@match, 'custom', @custom::jsonb)", new { match = s.Match, custom });
        var veto = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO match_map_vetos (match_id, tournament_id, team1_id, team2_id, status, best_of, toss_winner_team_id)
            VALUES (@match, @tournament, @a, @b, 'toss_choice_pending', 3, @a) RETURNING id
            """, new { match = s.Match, tournament = s.Tournament, a = teams.A, b = teams.B });
        async Task<JsonElement> Read() => await s.Client.GetFromJsonAsync<JsonElement>($"/api/air/broadcast/matches/{s.Match}/veto");

        var body = await Read();
        body.GetProperty("toss").GetProperty("pending").GetBoolean().Should().BeTrue();
        body.GetProperty("toss").GetProperty("winner").GetInt32().Should().Be(0);
        body.GetProperty("sequence").EnumerateArray().Select(x => $"{x.GetProperty("action").GetString()}:{x.GetProperty("team").GetInt32()}")
            .Should().Equal("ban:1", "ban:0", "pick:1", "pick:0", "ban:1", "ban:0", "decider:-1");

        // In progress: whose turn, and its timer.
        await conn.ExecuteAsync("UPDATE match_map_vetos SET status='in_progress', toss_completed_at=now(), current_team_id=@b, current_action='ban', turn_started_at=now(), turn_duration_seconds=45 WHERE id=@veto", new { veto, b = teams.B });
        body = await Read();
        body.GetProperty("turn").GetProperty("team").GetInt32().Should().Be(1);
        body.GetProperty("turn").GetProperty("seconds").GetInt32().Should().Be(45);
        body.GetProperty("toss").GetProperty("pending").GetBoolean().Should().BeFalse();

        // Finished: sides from pick_side, and the decider from the selected map even without its own action.
        var ids = new Dictionary<string, Guid>();
        foreach (var name in new[] { "Bind", "Abyss", "Lotus", "Haven", "Ascent", "Corrode", "Sunset" }) ids[name] = await MapId(conn, name);
        await conn.ExecuteAsync("""
            INSERT INTO match_map_veto_actions (veto_id, match_id, team_id, action_type, map_id, action_number, team_side, side) VALUES
              (@veto, @match, @b, 'ban', @bind, 1, 'team2', NULL), (@veto, @match, @a, 'ban', @abyss, 2, 'team1', NULL),
              (@veto, @match, @b, 'pick', @lotus, 3, 'team2', NULL), (@veto, @match, @a, 'pick_side', @lotus, 4, 'team1', 'attack'),
              (@veto, @match, @a, 'pick', @haven, 5, 'team1', NULL), (@veto, @match, @b, 'pick_side', @haven, 6, 'team2', 'defense'),
              (@veto, @match, @b, 'ban', @ascent, 7, 'team2', NULL), (@veto, @match, @a, 'ban', @corrode, 8, 'team1', NULL)
            """, new { veto, match = s.Match, a = teams.A, b = teams.B, bind = ids["Bind"], abyss = ids["Abyss"], lotus = ids["Lotus"], haven = ids["Haven"], ascent = ids["Ascent"], corrode = ids["Corrode"] });
        await conn.ExecuteAsync("UPDATE match_map_vetos SET status='completed', current_team_id=NULL, current_action=NULL, selected_map_id=@sunset WHERE id=@veto", new { veto, sunset = ids["Sunset"] });
        body = await Read();
        body.GetProperty("turn").ValueKind.Should().Be(JsonValueKind.Null);
        var steps = body.GetProperty("steps").EnumerateArray().ToArray();
        steps.Select(x => $"{x.GetProperty("team").GetInt32()}:{x.GetProperty("action").GetString()}:{x.GetProperty("map").GetString()}")
            .Should().Equal("1:ban:Bind", "0:ban:Abyss", "1:pick:Lotus", "0:pick:Haven", "1:ban:Ascent", "0:ban:Corrode", "-1:decider:Sunset");
        (steps[2].GetProperty("side").GetString(), steps[2].GetProperty("sideTeam").GetInt32()).Should().Be(("ATK", 0));
        (steps[3].GetProperty("side").GetString(), steps[3].GetProperty("sideTeam").GetInt32()).Should().Be(("DEF", 1));

        // The organizer resets the veto on Esportra: every action goes, and so does Air's copy.
        await conn.ExecuteAsync("DELETE FROM match_map_veto_actions WHERE veto_id=@veto; UPDATE match_map_vetos SET status='pending', selected_map_id=NULL WHERE id=@veto", new { veto });
        body = await Read();
        body.GetProperty("status").GetString().Should().Be("pending");
        body.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetRoster_ReturnsTheRegisteredLineupWithVerifiedRiotIds()
    {
        var s = await Seed();
        await using var conn = factory.Seeder.OpenConnection();
        var team = await conn.QuerySingleAsync<Guid>("SELECT team1_id FROM brkt_matches WHERE id=@id", new { id = s.Match });
        var players = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var player = Guid.NewGuid();
            await factory.Seeder.SeedAuthUserAsync(player);
            players.Add(player);
        }
        var puuid = $"puuid-{Guid.NewGuid():N}";
        await conn.ExecuteAsync("INSERT INTO player_riot_accounts (user_id, riot_puuid, game_name, tag_line, verified_at) VALUES (@user, @puuid, 'Orbit', 'NWL', now())", new { user = players[0], puuid });
        var lineup = JsonSerializer.Serialize(new { starters = players.Select((p, i) => new { displayName = $"Player {i + 1}", userId = p.ToString() }) });
        await conn.ExecuteAsync("INSERT INTO tournament_participants (tournament_id, user_id, team_id, status, participant_type, roster_lineup, team_members) VALUES (@tournament, @owner, @team, 'approved', 'team', @lineup::jsonb, '[]'::jsonb)", new { tournament = s.Tournament, owner = s.User, team, lineup });

        var body = await s.Client.GetFromJsonAsync<JsonElement>($"/api/air/broadcast/matches/{s.Match}/roster");

        var lineupOut = body.GetProperty("team1").GetProperty("lineup");
        lineupOut.GetArrayLength().Should().Be(5);
        lineupOut[0].GetProperty("name").GetString().Should().Be("Player 1");
        lineupOut[0].GetProperty("gameId").GetString().Should().Be(puuid);
        lineupOut[0].GetProperty("inGameName").GetString().Should().Be("Orbit#NWL");
        lineupOut[1].GetProperty("gameId").ValueKind.Should().Be(JsonValueKind.Null, "an unverified player has no Riot id");
        body.GetProperty("team2").GetProperty("lineup").GetArrayLength().Should().Be(0);
        body.GetProperty("team1").TryGetProperty("players", out _).Should().BeTrue("the earlier field stays for older Air versions");
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

    [Fact]
    public async Task VetoActions_LetAirRunTheMatchRoomVeto_InOrder_Once_AndNoticeTheWebsite()
    {
        var s = await Seed();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE brkt_matches SET best_of=3 WHERE id=@id", new { id = s.Match });
        var teams = await conn.QuerySingleAsync<(Guid A, Guid B)>("SELECT team1_id, team2_id FROM brkt_matches WHERE id=@id", new { id = s.Match });
        var names = new[] { "Abyss", "Ascent", "Bind", "Corrode", "Haven", "Lotus", "Sunset" };
        var pool = new List<string>();
        foreach (var name in names) pool.Add((await MapId(conn, name)).ToString());
        await conn.ExecuteAsync("""
            INSERT INTO match_map_vetos (match_id, tournament_id, team1_id, team2_id, status, best_of, game, toss_winner_team_id, selected_map_pool)
            VALUES (@match, @tournament, @a, @b, 'toss_choice_pending', 3, 'valorant', @b, @pool)
            """, new { match = s.Match, tournament = s.Tournament, a = teams.A, b = teams.B, pool = pool.ToArray() });
        var url = $"/api/air/broadcast/matches/{s.Match}/veto/actions";
        async Task<(HttpStatusCode Status, JsonElement Body)> Act(string key, int? step, string action, string? map = null, string? side = null, bool? goFirst = null)
        {
            var res = await s.Client.PostAsJsonAsync(url, new { clientKey = key, expectedStep = step, action, map, side, goFirst });
            var text = await res.Content.ReadAsStringAsync();
            return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement : default);
        }

        // The toss winner (team 2) defers: team 1 bans first.
        var toss = await Act("toss-0001", 0, "toss_choice", goFirst: false);
        toss.Status.Should().Be(HttpStatusCode.OK, toss.Body.ToString());
        toss.Body.GetProperty("turn").GetProperty("team").GetInt32().Should().Be(0);
        var again = await Act("toss-0001", 0, "toss_choice", goFirst: false);
        again.Body.ToString().Should().Be(toss.Body.ToString(), "a retry gets the first answer");

        // Walk the veto as the match room asks for it: bans and picks from the pool, a side after every pick and on the decider.
        var left = new Queue<string>(names);
        var body = toss.Body;
        for (var n = 0; n < 12 && body.GetProperty("status").GetString() != "completed"; n++)
        {
            var turn = body.GetProperty("turn");
            var action = turn.GetProperty("action").GetString()!;
            var step = body.GetProperty("step").GetInt32();
            var map = action == "pick_side" ? null : left.Dequeue();
            if (action == "pick_side") map = SideMap(body);
            var r = await Act($"step-{n:0000}", step, action, map, action == "pick_side" ? (n % 2 == 0 ? "ATK" : "DEF") : null);
            r.Status.Should().Be(HttpStatusCode.OK, $"{action} {map}: {r.Body}");
            body = r.Body;
        }
        body.GetProperty("status").GetString().Should().Be("completed");
        var steps = body.GetProperty("steps").EnumerateArray().ToArray();
        steps.Count(x => x.GetProperty("action").GetString() == "ban").Should().Be(4);
        steps.Where(x => x.GetProperty("action").GetString() != "ban").Should().OnlyContain(x => x.GetProperty("side").GetString() == "ATK" || x.GetProperty("side").GetString() == "DEF", "every pick and the decider carry a side");
        steps.Should().Contain(x => x.GetProperty("action").GetString() == "decider");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM match_map_veto_actions WHERE match_id=@match", new { match = s.Match })).Should().BeGreaterThan(8);

        // A step made on the website in between: Air's old step number is refused with the current veto.
        var stale = await Act("stale-0001", 1, "ban", "Abyss");
        stale.Status.Should().Be(HttpStatusCode.Conflict);
        stale.Body.GetProperty("veto").GetProperty("status").GetString().Should().Be("completed");

        // Reset clears the match room's veto.
        (await Act("reset-0001", null, "reset")).Status.Should().Be(HttpStatusCode.OK);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM match_map_veto_actions WHERE match_id=@match", new { match = s.Match })).Should().Be(0);
    }

    [Fact]
    public async Task VetoActions_RefuseOtherAccounts_UnknownMaps_AndBadRequests()
    {
        var s = await Seed();
        await using var conn = factory.Seeder.OpenConnection();
        var teams = await conn.QuerySingleAsync<(Guid A, Guid B)>("SELECT team1_id, team2_id FROM brkt_matches WHERE id=@id", new { id = s.Match });
        var pool = new[] { (await MapId(conn, "Bind")).ToString(), (await MapId(conn, "Lotus")).ToString() };
        await conn.ExecuteAsync("""
            INSERT INTO match_map_vetos (match_id, tournament_id, team1_id, team2_id, status, best_of, game, current_team_id, current_action, current_action_number, selected_map_pool)
            VALUES (@match, @tournament, @a, @b, 'in_progress', 1, 'valorant', @a, 'ban', 1, @pool)
            """, new { match = s.Match, tournament = s.Tournament, a = teams.A, b = teams.B, pool });
        var url = $"/api/air/broadcast/matches/{s.Match}/veto/actions";
        (await s.Client.PostAsJsonAsync(url, new { clientKey = "short", action = "ban", map = "Bind" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await s.Client.PostAsJsonAsync(url, new { clientKey = "key-00001", action = "pick_side", map = "Bind", side = "LEFT" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await s.Client.PostAsJsonAsync(url, new { clientKey = "key-00002", expectedStep = 1, action = "ban", map = "Haven" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "Haven is not in the pool");
        var outsider = await Seed();
        (await outsider.Client.PostAsJsonAsync(url, new { clientKey = "key-00003", action = "ban", map = "Bind" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var noDevice = factory.CreateAuthenticatedClient(s.User);
        (await noDevice.PostAsJsonAsync(url, new { clientKey = "key-00004", action = "ban", map = "Bind" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The map a side is chosen on now: the last pick without a side, or the decider (the map left).</summary>
    private static string SideMap(JsonElement body)
    {
        var steps = body.GetProperty("steps").EnumerateArray().ToArray();
        var open = steps.LastOrDefault(x => x.GetProperty("action").GetString() == "pick" && x.GetProperty("side").ValueKind == JsonValueKind.Null);
        if (open.ValueKind != JsonValueKind.Undefined) return open.GetProperty("map").GetString()!;
        var used = steps.Select(x => x.GetProperty("map").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[] { "Abyss", "Ascent", "Bind", "Corrode", "Haven", "Lotus", "Sunset" }.First(m => !used.Contains(m));
    }
}
