using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.ProviderMatches;

[Collection(IntegrationCollection.Name)]
public sealed class ProviderMatchPipelineTests(ApiFactory factory)
{
    private sealed record Setup(Guid Team1, Guid Team2, Guid SessionId, Guid[] Users, string Prefix)
    {
        /// <summary>Fixture JSON with this test's PUUIDs (riot_accounts.puuid is unique across the shared test database).</summary>
        public string Payload(string matchId, IEnumerable<Round>? rounds = null) =>
            System.Text.RegularExpressions.Regex.Replace(
                Build(matchId: matchId, rounds: rounds).GetRawText(), "\"p(\\d+)\"", m => $"\"{Prefix}p{m.Groups[1].Value}\"");
    }

    private static readonly Round[] Rounds =
    [
        new(0, "Blue", "Attacker", [new Kill("p1", "p6", 5_000, 5_000), new Kill("p2", "p7", 9_000, 9_000)],
            Damage: [new Dmg("p1", "p6", 150, 1, 0, 0), new Dmg("p2", "p7", 120, 0, 1, 0)], Planter: "p1"),
        new(1, "Red", "Defender", [new Kill("p6", "p1", 4_000, 100_000)]),
    ];

    /// <summary>Two teams of five with linked Riot accounts p1..p10 (p1-p5 team 1) and an in-progress scrim.</summary>
    private async Task<Setup> SeedAsync(int linkedPerTeam = 5)
    {
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var u in users) await factory.Seeder.SeedAuthUserAsync(u);
        var team1 = await factory.Seeder.SeedTeamAsync(users[0], "Alpha " + Guid.NewGuid().ToString("N")[..6]);
        var team2 = await factory.Seeder.SeedTeamAsync(users[5], "Bravo " + Guid.NewGuid().ToString("N")[..6]);
        await using var conn = factory.Seeder.OpenConnection();
        for (var i = 0; i < 10; i++)
        {
            var team = i < 5 ? team1 : team2;
            await conn.ExecuteAsync(
                "INSERT INTO public.team_members (team_id, user_id, role) VALUES (@team, @user, 'member') ON CONFLICT (team_id, user_id) DO NOTHING",
                new { team, user = users[i] });
            if (i % 5 < linkedPerTeam)
                await conn.ExecuteAsync(
                    "INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@user, @puuid, @name, 'EUW')",
                    new { user = users[i], puuid = $"{prefix}p{i + 1}", name = $"Player{i + 1}" });
        }
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@team1, @team2, 'valorant', 'in_progress') RETURNING id",
            new { team1, team2 });
        return new Setup(team1, team2, session, users, prefix);
    }

    private async Task<Guid> SeedGameAsync(Setup s, int number, string riotMatchId, int t1 = 13, int t2 = 7)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.scrim_session_games (session_id, game_number, team1_score, team2_score, source, riot_match_id, reported_by_team_id)
            VALUES (@sessionId, @number, @t1, @t2, 'riot', @riotMatchId, @team1) RETURNING id
            """, new { sessionId = s.SessionId, number, t1, t2, riotMatchId, team1 = s.Team1 });
    }

    private static ProviderMatchRegistration Reg(Setup s, Guid gameId, string riotMatchId, int t1 = 13, int t2 = 7, string? map = "Ascent") =>
        new(ProviderMatchContext.ScrimGame, gameId, null, 1, riotMatchId, s.Team1, s.Team2, t1, t2, map);

    private static string NewMatchId() => Guid.NewGuid().ToString();

    private async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await conn.ExecuteScalarAsync<int>(sql, args);
    }

    [Fact]
    public async Task Reported_match_is_ingested_verified_and_the_roster_is_frozen()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();

        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));
        reg!.NeedsIngest.Should().BeTrue();
        var payload = s.Payload(matchId, Rounds);
        var outcome = await ProviderMatchIngest.ApplyAsync(conn, reg.AssociationId, payload, "eu", storePayload: true);

        outcome.VerificationStatus.Should().Be("verified");
        var a = await conn.QuerySingleAsync<(string Status, string T1, string T2)>(
            "SELECT verification_status, team1_riot_side, team2_riot_side FROM public.provider_match_associations WHERE id = @id", new { id = reg.AssociationId });
        a.Should().Be(("verified", "Blue", "Red"));
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.provider_match_roster_snapshots WHERE association_id = @id AND played", new { id = reg.AssociationId })).Should().Be(10);
        (await conn.QuerySingleAsync<string>("SELECT data_status FROM public.provider_matches WHERE provider_match_id = @matchId", new { matchId })).Should().Be("retrieved");
        (await conn.ExecuteScalarAsync<int>("SELECT first_kills FROM public.valorant_match_players WHERE puuid = @p1 AND match_id = (SELECT id FROM public.provider_matches WHERE provider_match_id = @matchId)", new { matchId, p1 = s.Prefix + "p1" })).Should().Be(1);

        // The roster as reported stays put when the team changes afterwards.
        await conn.ExecuteAsync("UPDATE public.team_members SET is_active = FALSE WHERE team_id = @t AND user_id = @u", new { t = s.Team1, u = s.Users[4] });
        await conn.ExecuteAsync("UPDATE public.riot_accounts SET puuid = @changed WHERE user_id = @u", new { u = s.Users[4], changed = s.Prefix + "changed" });
        await ProviderMatchIngest.ApplyAsync(conn, reg.AssociationId, payload, "eu", storePayload: false);
        var frozen = await conn.QuerySingleAsync<(int Slot, string Puuid, string Identity)>(
            "SELECT team_slot::int, puuid, identity_status FROM public.provider_match_roster_snapshots WHERE association_id = @id AND user_id = @u",
            new { id = reg.AssociationId, u = s.Users[4] });
        frozen.Should().Be((1, s.Prefix + "p5", "linked"));
    }

    [Fact]
    public async Task Ingesting_twice_leaves_identical_rows_and_a_single_event()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));
        var payload = s.Payload(matchId, Rounds);

        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, payload, "eu", true);
        var counts1 = await CountsAsync(conn, matchId);
        await ProviderMatchIngest.ApplyAsync(conn, reg.AssociationId, payload, "eu", true);
        await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));
        var counts2 = await CountsAsync(conn, matchId);

        counts2.Should().Be(counts1);
        counts1.Should().Be((1, 10, 2, 3, 2, 2, 1));
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.provider_match_association_events WHERE association_id = @id AND event_type = 'verification'", new { id = reg.AssociationId })).Should().Be(1);
    }

    private static async Task<(int Matches, int Players, int Rounds, int Kills, int Damage, int Teams, int Payloads)> CountsAsync(Npgsql.NpgsqlConnection conn, string matchId) =>
        await conn.QuerySingleAsync<(int, int, int, int, int, int, int)>(
            """
            WITH m AS (SELECT id FROM public.provider_matches WHERE provider_match_id = @matchId)
            SELECT (SELECT count(*) FROM m)::int,
                   (SELECT count(*) FROM public.valorant_match_players WHERE match_id IN (SELECT id FROM m))::int,
                   (SELECT count(*) FROM public.valorant_match_rounds WHERE match_id IN (SELECT id FROM m))::int,
                   (SELECT count(*) FROM public.valorant_match_kills WHERE match_id IN (SELECT id FROM m))::int,
                   (SELECT count(*) FROM public.valorant_round_damage WHERE match_id IN (SELECT id FROM m))::int,
                   (SELECT count(*) FROM public.valorant_match_teams WHERE match_id IN (SELECT id FROM m))::int,
                   (SELECT count(*) FROM public.provider_match_payloads WHERE match_id IN (SELECT id FROM m))::int
            """, new { matchId });

    [Fact]
    public async Task The_same_riot_match_cannot_count_for_two_games()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var game1 = await SeedGameAsync(s, 1, matchId);
        var game2 = await SeedGameAsync(s, 2, matchId);
        await using var conn = factory.Seeder.OpenConnection();

        var first = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, game1, matchId));
        var second = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, game2, matchId));

        first!.NeedsIngest.Should().BeTrue();
        second!.NeedsIngest.Should().BeFalse();
        (await conn.QuerySingleAsync<string>("SELECT verification_status FROM public.provider_match_associations WHERE id = @id", new { id = second.AssociationId })).Should().Be("duplicate");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.provider_matches WHERE provider_match_id = @matchId", new { matchId })).Should().Be(1);
    }

    [Fact]
    public async Task Changing_the_reported_match_is_logged_and_resets_verification()
    {
        var s = await SeedAsync();
        var first = NewMatchId();
        var second = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, first);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, first));
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, s.Payload(first, Rounds), "eu", true);

        var again = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, second));

        again!.AssociationId.Should().Be(reg.AssociationId);
        (await conn.QuerySingleAsync<string>("SELECT verification_status FROM public.provider_match_associations WHERE id = @id", new { id = reg.AssociationId })).Should().Be("pending");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.provider_match_association_events WHERE association_id = @id AND event_type = 'match_changed'", new { id = reg.AssociationId })).Should().Be(1);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.provider_match_roster_snapshots WHERE association_id = @id AND played IS NOT NULL", new { id = reg.AssociationId })).Should().Be(0);
    }

    [Fact]
    public async Task Unlinked_players_make_the_match_partially_verified_and_are_not_invented()
    {
        var s = await SeedAsync(linkedPerTeam: 3);
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));

        var outcome = await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, s.Payload(matchId, Rounds), "eu", true);

        outcome.VerificationStatus.Should().Be("partially_verified");
        var rows = (await conn.QueryAsync<(string? Puuid, string Identity, bool? Played)>(
            "SELECT puuid, identity_status, played FROM public.provider_match_roster_snapshots WHERE association_id = @id", new { id = reg.AssociationId })).ToList();
        rows.Count(r => r.Identity == "unlinked" && r.Played == null).Should().Be(4);
        rows.Count(r => r.Identity == "unmatched" && r.Played == true).Should().Be(4);
        rows.Where(r => r.Identity == "unlinked").Should().OnlyContain(r => r.Puuid == null);
    }

    [Fact]
    public async Task A_wrong_score_is_disputed_not_verified()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId, t1: 13, t2: 11);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId, t1: 13, t2: 11));

        var outcome = await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, s.Payload(matchId, Rounds), "eu", true);

        outcome.VerificationStatus.Should().Be("disputed");
        (await conn.QuerySingleAsync<string>("SELECT verification_detail::text FROM public.provider_match_associations WHERE id = @id", new { id = reg.AssociationId })).Should().Contain("team2_score_mismatch");
    }

    [Fact]
    public async Task A_payload_for_a_different_match_is_rejected()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));

        var outcome = await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, s.Payload("someone-elses-match-1"), "eu", true);

        outcome.Failure.Should().Be("match_id_mismatch");
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.valorant_match_players WHERE match_id = (SELECT id FROM public.provider_matches WHERE provider_match_id = @matchId)", new { matchId })).Should().Be(0);
        (await conn.QuerySingleAsync<string>("SELECT data_status FROM public.provider_matches WHERE provider_match_id = @matchId", new { matchId })).Should().Be("failed");
    }

    [Fact]
    public async Task Registration_never_creates_a_match_from_a_malformed_id()
    {
        var s = await SeedAsync();
        var gameId = await SeedGameAsync(s, 1, "x");
        await using var conn = factory.Seeder.OpenConnection();
        (await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, "../../etc"))).Should().BeNull();
    }

    [Fact]
    public async Task Events_are_append_only()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));

        var update = async () => await conn.ExecuteAsync("UPDATE public.provider_match_association_events SET event_type = 'x' WHERE association_id = @id", new { id = reg!.AssociationId });
        var delete = async () => await conn.ExecuteAsync("DELETE FROM public.provider_match_association_events WHERE association_id = @id", new { id = reg!.AssociationId });
        await update.Should().ThrowAsync<Exception>();
        await delete.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_new_tables_are_closed_to_clients()
    {
        await using var conn = factory.Seeder.OpenConnection();
        foreach (var role in new[] { "authenticated", "anon" })
            foreach (var table in new[] { "provider_matches", "provider_match_payloads", "provider_match_associations", "provider_match_roster_snapshots", "valorant_match_players", "valorant_match_kills" })
            {
                await using var tx = await conn.BeginTransactionAsync();
                await conn.ExecuteAsync($"SET LOCAL ROLE {role}", transaction: tx);
                var read = async () => await conn.ExecuteScalarAsync<int>($"SELECT count(*)::int FROM public.{table}", transaction: tx);
                await read.Should().ThrowAsync<Exception>($"{role} must not read {table}");
            }
    }

    [Fact]
    public async Task Without_a_linked_account_the_match_is_marked_unavailable_and_riot_is_not_called()
    {
        var s = await SeedAsync(linkedPerTeam: 0);
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));
        var riot = new FakeRiot();

        await riot.RunJobAsync(factory, reg!.AssociationId, attempt: 0);

        riot.Requests.Should().Be(0);
        (await conn.QuerySingleAsync<string>("SELECT data_status FROM public.provider_matches WHERE provider_match_id = @matchId", new { matchId })).Should().Be("unavailable");
        (await conn.QuerySingleAsync<string>("SELECT verification_status FROM public.provider_match_associations WHERE id = @id", new { id = reg.AssociationId })).Should().Be("unverified");
    }

    [Fact]
    public async Task The_job_fetches_on_the_server_stores_the_payload_and_verifies()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));
        var riot = new FakeRiot { MatchBody = s.Payload(matchId, Rounds) };

        await riot.RunJobAsync(factory, reg!.AssociationId, attempt: 0);

        (await conn.QuerySingleAsync<string>("SELECT verification_status FROM public.provider_match_associations WHERE id = @id", new { id = reg.AssociationId })).Should().Be("verified");
        (await CountsAsync(conn, matchId)).Payloads.Should().Be(1);

        // A stored payload is re-derived without asking Riot again.
        var before = riot.Requests;
        await riot.RunJobAsync(factory, reg.AssociationId, attempt: 0);
        riot.Requests.Should().Be(before);
    }

    [Fact]
    public async Task A_match_riot_has_not_listed_yet_is_looked_for_again()
    {
        var s = await SeedAsync();
        var matchId = NewMatchId();
        var gameId = await SeedGameAsync(s, 1, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, Reg(s, gameId, matchId));
        var riot = new FakeRiot { MatchStatus = System.Net.HttpStatusCode.NotFound };

        await riot.RunJobAsync(factory, reg!.AssociationId, attempt: 0);

        riot.Scheduled.Should().Be(1);
        (await conn.QuerySingleAsync<string>("SELECT data_status FROM public.provider_matches WHERE provider_match_id = @matchId", new { matchId })).Should().Be("pending");

        await riot.RunJobAsync(factory, reg.AssociationId, attempt: ProviderMatchIngestJob.RetryDelays.Length);
        (await conn.QuerySingleAsync<string>("SELECT data_status FROM public.provider_matches WHERE provider_match_id = @matchId", new { matchId })).Should().Be("unavailable");
    }
}
