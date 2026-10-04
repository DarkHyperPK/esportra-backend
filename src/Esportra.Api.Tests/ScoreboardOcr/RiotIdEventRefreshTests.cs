using System.Net;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Database;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

/// <summary>Riot IDs re-read at registration, check-in and sign-in, and the slower scheduled sweep.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class RiotIdEventRefreshTests(ApiFactory factory)
{
    private sealed class RecordingTrigger : IRiotIdRefreshTrigger
    {
        public List<string> Calls { get; } = [];

        public void TournamentEntry(Guid tournamentId, Guid userId) => Calls.Add($"entry:{tournamentId}:{userId}");

        public void Competitors(params Guid[] competitorIds) => Calls.Add($"competitors:{string.Join(',', competitorIds)}");

        public void Users(params Guid[] userIds) => Calls.Add($"users:{string.Join(',', userIds)}");
    }

    private sealed class FakeRiot(Func<string, RiotAccountLookupResult> answer) : IRiotAccountLookup
    {
        public List<string> Calls { get; } = [];

        public Task<RiotAccountLookupResult> GetByPuuidAsync(string puuid, CancellationToken ct)
        {
            Calls.Add(puuid);
            return Task.FromResult(answer(puuid));
        }
    }

    private RiotIdRefreshService Service(IRiotAccountLookup riot) =>
        new(factory.Services.GetRequiredService<IDbConnectionFactory>(), riot, NullLogger<RiotIdRefreshService>.Instance);

    private static FakeRiot Unchanged() => new(_ => new RiotAccountLookupResult(404, null, null));

    private async Task<(Guid UserId, string Puuid)> SeedLinkedUserAsync(string prefix, string? checkedAgo = null)
    {
        var userId = Guid.NewGuid();
        var puuid = $"puuid-{userId:N}";
        await factory.Seeder.SeedAuthUserAsync(userId);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.ExecuteAsync(
            $"""
            INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line, name_checked_at)
            VALUES (@userId, @puuid, @name, 'EU1', {(checkedAgo is null ? "NULL" : $"now() - interval '{checkedAgo}'")})
            """,
            new { userId, puuid, name = $"{prefix}{Guid.NewGuid():N}"[..12] });
        return (userId, puuid);
    }

    private async Task<Guid> SeedCheckInTournamentAsync(Guid playerId)
    {
        var organizer = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(organizer);
        var tournamentId = await factory.Seeder.SeedTournamentAsync(organizer);
        await factory.Seeder.SeedParticipantAsync(playerId, tournamentId);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.ExecuteAsync("UPDATE tournaments SET check_in_required = TRUE WHERE id = @tournamentId", new { tournamentId });
        return tournamentId;
    }

    private static HttpClient Authenticated(WebApplicationFactory<Program> app, Guid userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }

    [Fact]
    public async Task CheckIn_QueuesARefreshOfTheEntry()
    {
        var (userId, _) = await SeedLinkedUserAsync("Chk");
        var tournamentId = await SeedCheckInTournamentAsync(userId);
        var trigger = new RecordingTrigger();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IRiotIdRefreshTrigger>(_ => trigger)));

        var response = await Authenticated(app, userId).PostAsync($"/api/tournaments/{tournamentId}/check-in", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        trigger.Calls.Should().Equal($"entry:{tournamentId}:{userId}");
    }

    [Fact]
    public async Task CheckIn_SucceedsWithTheRealBackgroundQueue()
    {
        var (userId, _) = await SeedLinkedUserAsync("Bgq");
        var tournamentId = await SeedCheckInTournamentAsync(userId);

        var response = await Authenticated(factory, userId).PostAsync($"/api/tournaments/{tournamentId}/check-in", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TournamentEntry_RefreshesEveryActiveTeamMember()
    {
        var captain = await SeedLinkedUserAsync("Cap");
        var member = await SeedLinkedUserAsync("Mem");
        var outsider = await SeedLinkedUserAsync("Out");
        var team = await factory.Seeder.SeedTeamAsync(captain.UserId, "Refresh");
        var organizer = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(organizer);
        var tournamentId = await factory.Seeder.SeedTournamentAsync(organizer);
        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO team_members (team_id, user_id, role, joined_at) VALUES (@team, @member, 'member', NOW());
                INSERT INTO tournament_participants (id, tournament_id, user_id, team_id, team_captain_id, status, participant_type)
                VALUES (gen_random_uuid(), @tournamentId, @captain, @team, @captain, 'approved', 'team');
                """,
                new { team, member = member.UserId, tournamentId, captain = captain.UserId });
        }
        var riot = Unchanged();

        await Service(riot).RefreshTournamentEntryAsync(tournamentId, captain.UserId, TimeSpan.FromHours(1), CancellationToken.None);

        riot.Calls.Should().BeEquivalentTo([captain.Puuid, member.Puuid]);
        riot.Calls.Should().NotContain(outsider.Puuid);
    }

    [Fact]
    public async Task Sweep_TakesNeverCheckedAndWeekOldAccounts_NotRecentlyCheckedIdleOnes()
    {
        var never = await SeedLinkedUserAsync("Nev");
        var weekOld = await SeedLinkedUserAsync("Wk", "8 days");
        var idle = await SeedLinkedUserAsync("Idl", "2 days");
        var riot = Unchanged();

        await Service(riot).RefreshStaleAsync(10_000, CancellationToken.None);

        riot.Calls.Should().Contain([never.Puuid, weekOld.Puuid]);
        riot.Calls.Should().NotContain(idle.Puuid);
    }

    [Fact]
    public async Task Sweep_TakesAPlayerInARunningTournamentAfterADay()
    {
        var playing = await SeedLinkedUserAsync("Ply", "2 days");
        var organizer = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(organizer);
        var tournamentId = await factory.Seeder.SeedTournamentAsync(organizer, status: "ongoing");
        await factory.Seeder.SeedParticipantAsync(playing.UserId, tournamentId);
        var riot = Unchanged();

        await Service(riot).RefreshStaleAsync(10_000, CancellationToken.None);

        riot.Calls.Should().Contain(playing.Puuid);
    }

    [Fact]
    public async Task SignInWithANewName_KeepsThePreviousOne()
    {
        var (userId, puuid) = await SeedLinkedUserAsync("Sgn");
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        var old = await conn.QuerySingleAsync<string>("SELECT game_name FROM riot_accounts WHERE puuid = @puuid", new { puuid });

        await RiotIdRefreshService.RecordSignInNameAsync(conn, userId, puuid, old, "EU1");
        await RiotIdRefreshService.RecordSignInNameAsync(conn, userId, puuid, "Renamed", "EU1");

        (await conn.QueryAsync<string>("SELECT game_name FROM riot_account_name_history WHERE puuid = @puuid", new { puuid }))
            .Should().Equal(old);
    }
}
