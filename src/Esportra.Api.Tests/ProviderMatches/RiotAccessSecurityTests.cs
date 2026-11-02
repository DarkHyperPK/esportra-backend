using System.Net;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ProviderMatches;

[Collection(IntegrationCollection.Name)]
public sealed class RiotAccessSecurityTests(ApiFactory factory)
{
    private async Task<Guid> UserWithRiotAsync(string puuid)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@id, @puuid, 'N', 'T')", new { id, puuid });
        await conn.ExecuteAsync("INSERT INTO public.player_riot_accounts (user_id, riot_puuid, game_name, tag_line) VALUES (@id, @puuid, 'N', 'T')", new { id, puuid });
        return id;
    }

    [Fact]
    public async Task The_raw_riot_proxy_is_staff_only()
    {
        var user = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(user);
        var body = new { region = "na", endpoint = "/val/match/v1/matches/abcdefgh-1" };

        (await factory.CreateClient().PostAsJsonAsync("/api/integrations/riot/proxy", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateAuthenticatedClient(user).PostAsJsonAsync("/api/integrations/riot/proxy", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enriched_match_refuses_matches_the_caller_has_no_claim_to_before_calling_riot()
    {
        var user = await UserWithRiotAsync("sec-" + Guid.NewGuid().ToString("N"));
        var client = factory.CreateAuthenticatedClient(user);

        // A linked account may ask Riot whether it played in a match, but never receives a match it was not in.
        var unrelated = await client.PostAsJsonAsync("/api/integrations/riot/enriched-match", new { region = "na", matchId = Guid.NewGuid().ToString() });
        unrelated.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await unrelated.Content.ReadAsStringAsync()).Should().NotContain("matchInfo");

        (await client.PostAsJsonAsync("/api/integrations/riot/enriched-match", new { region = "na", matchId = "../../riot/account/v1/accounts/me" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await factory.CreateAuthenticatedClient(stranger).PostAsJsonAsync("/api/integrations/riot/enriched-match", new { region = "na", matchId = Guid.NewGuid().ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_scrim_match_is_visible_only_to_the_two_teams()
    {
        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        foreach (var u in new[] { owner1, owner2, outsider }) await factory.Seeder.SeedAuthUserAsync(u);
        var team1 = await factory.Seeder.SeedTeamAsync(owner1, "T1 " + Guid.NewGuid().ToString("N")[..6]);
        var team2 = await factory.Seeder.SeedTeamAsync(owner2, "T2 " + Guid.NewGuid().ToString("N")[..6]);
        var matchId = Guid.NewGuid().ToString();
        await using var conn = factory.Seeder.OpenConnection();
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@team1, @team2, 'valorant', 'in_progress') RETURNING id", new { team1, team2 });
        await conn.ExecuteAsync(
            "INSERT INTO public.scrim_session_games (session_id, game_number, team1_score, team2_score, riot_match_id, reported_by_team_id) VALUES (@session, 1, 13, 7, @matchId, @team1)",
            new { session, matchId, team1 });

        (await RiotMatchAccess.IsReportedAndVisibleAsync(conn, matchId, owner1)).Should().BeTrue();
        (await RiotMatchAccess.IsReportedAndVisibleAsync(conn, matchId, owner2)).Should().BeTrue();
        (await RiotMatchAccess.IsReportedAndVisibleAsync(conn, matchId, outsider)).Should().BeFalse();
        (await RiotMatchAccess.IsReportedAndVisibleAsync(conn, Guid.NewGuid().ToString(), owner1)).Should().BeFalse();
    }

    [Fact]
    public async Task Riot_account_rows_are_private_to_their_owner_and_hold_no_tokens()
    {
        var a = await UserWithRiotAsync("rls-a-" + Guid.NewGuid().ToString("N"));
        var b = await UserWithRiotAsync("rls-b-" + Guid.NewGuid().ToString("N"));
        await using var conn = factory.Seeder.OpenConnection();

        (await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'riot_accounts' AND column_name IN ('access_token', 'refresh_token', 'token_expires_at')"))
            .Should().Be(0);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            // Simulate Supabase's default table grants, so the policies (not missing privileges) are what is tested.
            await conn.ExecuteAsync("GRANT SELECT, INSERT, UPDATE, DELETE ON public.riot_accounts TO authenticated", transaction: tx);
            await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
            await conn.ExecuteAsync("SELECT set_config('request.jwt.claim.sub', @a, true)", new { a = a.ToString() }, tx);
            var ids = (await conn.QueryAsync<Guid>("SELECT user_id FROM public.riot_accounts", transaction: tx)).ToList();
            ids.Should().Equal(a);
            ids.Should().NotContain(b);
            (await conn.ExecuteAsync("UPDATE public.riot_accounts SET puuid = 'stolen' WHERE user_id = @a", new { a }, tx)).Should().Be(0);
            var insert = async () => await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@a, 'forged', 'N', 'T')", new { a }, tx);
            await insert.Should().ThrowAsync<Exception>();
        }
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("GRANT SELECT ON public.riot_accounts TO anon", transaction: tx);
            await conn.ExecuteAsync("SET LOCAL ROLE anon", transaction: tx);
            (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.riot_accounts", transaction: tx)).Should().Be(0);
        }
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("GRANT SELECT ON public.riot_account_name_history TO authenticated", transaction: tx);
            await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
            (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.riot_account_name_history", transaction: tx)).Should().Be(0);
        }
    }

    [Fact]
    public async Task Unlinking_removes_every_copy_of_the_riot_identity()
    {
        var user = await UserWithRiotAsync("unlink-" + Guid.NewGuid().ToString("N"));

        var response = await factory.CreateAuthenticatedClient(user).DeleteAsync("/api/integrations/riot");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.riot_accounts WHERE user_id = @user", new { user })).Should().Be(0);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.player_riot_accounts WHERE user_id = @user", new { user })).Should().Be(0);
    }
}
