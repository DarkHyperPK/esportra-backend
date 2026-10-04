using Dapper;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Database;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

[Collection(IntegrationCollection.Name)]
public sealed class RiotIdRefreshTests(ApiFactory factory)
{
    private sealed class FakeRiot(Func<string, RiotAccountLookupResult> answer) : IRiotAccountLookup
    {
        public List<string> Calls { get; } = [];

        public Task<RiotAccountLookupResult> GetByPuuidAsync(string puuid, CancellationToken ct)
        {
            Calls.Add(puuid);
            return Task.FromResult(answer(puuid));
        }
    }

    private IDbConnectionFactory Db => factory.Services.GetRequiredService<IDbConnectionFactory>();

    private RiotIdRefreshService Service(IRiotAccountLookup riot) => new(Db, riot, NullLogger<RiotIdRefreshService>.Instance);

    private async Task<(Guid UserId, string Puuid)> SeedLinkedUserAsync(string gameName, string tagLine)
    {
        var userId = Guid.NewGuid();
        var puuid = $"puuid-{userId:N}";
        await factory.Seeder.SeedAuthUserAsync(userId);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@userId, @puuid, @gameName, @tagLine);
            INSERT INTO player_riot_accounts (user_id, riot_puuid, game_name, tag_line) VALUES (@userId, @puuid, @gameName, @tagLine);
            UPDATE profiles SET riot_tag = @tag WHERE id = @userId;
            """,
            new { userId, puuid, gameName, tagLine, tag = $"{gameName}#{tagLine}" });
        return (userId, puuid);
    }

    [Fact]
    public async Task Rename_UpdatesLinkedAccountsAndProfile_AndKeepsTheOldName()
    {
        var old = $"Old{Guid.NewGuid():N}"[..12];
        var renamed = $"New{Guid.NewGuid():N}"[..12];
        var (userId, puuid) = await SeedLinkedUserAsync(old, "EU1");
        var riot = new FakeRiot(_ => new RiotAccountLookupResult(200, renamed, "EU2"));

        var summary = await Service(riot).RefreshUsersAsync([userId], TimeSpan.FromHours(6), CancellationToken.None);

        summary.Renamed.Should().Be(1);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        (await conn.QuerySingleAsync<string>("SELECT game_name || '#' || tag_line FROM riot_accounts WHERE puuid = @puuid", new { puuid }))
            .Should().Be($"{renamed}#EU2");
        (await conn.QuerySingleAsync<string>("SELECT game_name FROM player_riot_accounts WHERE riot_puuid = @puuid", new { puuid }))
            .Should().Be(renamed);
        (await conn.QuerySingleAsync<string>("SELECT riot_tag FROM profiles WHERE id = @userId", new { userId }))
            .Should().Be($"{renamed}#EU2");
        (await conn.QuerySingleAsync<string>("SELECT game_name FROM riot_account_name_history WHERE puuid = @puuid", new { puuid }))
            .Should().Be(old);
    }

    [Fact]
    public async Task RecentlyChecked_IsNotAskedAgain()
    {
        var (userId, _) = await SeedLinkedUserAsync($"Same{Guid.NewGuid():N}"[..12], "EU1");
        var riot = new FakeRiot(p => new RiotAccountLookupResult(404, null, null));

        await Service(riot).RefreshUsersAsync([userId], TimeSpan.FromHours(6), CancellationToken.None);
        await Service(riot).RefreshUsersAsync([userId], TimeSpan.FromHours(6), CancellationToken.None);

        riot.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task RateLimited_StopsTheBatch()
    {
        var first = await SeedLinkedUserAsync($"Rl1{Guid.NewGuid():N}"[..12], "EU1");
        var second = await SeedLinkedUserAsync($"Rl2{Guid.NewGuid():N}"[..12], "EU1");
        var riot = new FakeRiot(_ => new RiotAccountLookupResult(429, null, null));

        var summary = await Service(riot).RefreshUsersAsync([first.UserId, second.UserId], TimeSpan.FromHours(6), CancellationToken.None);

        summary.RateLimited.Should().BeTrue();
        riot.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task RenameToATagAnotherProfileHolds_LeavesTheProfileTagAlone()
    {
        var taken = $"Tkn{Guid.NewGuid():N}"[..12];
        var holder = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(holder);
        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
            await conn.ExecuteAsync("UPDATE profiles SET riot_tag = @tag WHERE id = @holder", new { tag = $"{taken}#EU1", holder });
        var old = $"Old{Guid.NewGuid():N}"[..12];
        var (userId, puuid) = await SeedLinkedUserAsync(old, "EU1");

        var summary = await Service(new FakeRiot(_ => new RiotAccountLookupResult(200, taken, "EU1")))
            .RefreshUsersAsync([userId], TimeSpan.FromHours(6), CancellationToken.None);

        summary.Renamed.Should().Be(1);
        await using var check = new NpgsqlConnection(factory.ConnectionString);
        (await check.QuerySingleAsync<string>("SELECT game_name FROM riot_accounts WHERE puuid = @puuid", new { puuid })).Should().Be(taken);
        (await check.QuerySingleAsync<string>("SELECT riot_tag FROM profiles WHERE id = @userId", new { userId })).Should().Be($"{old}#EU1");
    }

    [Fact]
    public async Task RosterLoader_TagsEachNameWithItsSource()
    {
        var (userId, puuid) = await SeedLinkedUserAsync($"Ver{Guid.NewGuid():N}"[..12], "EU1");
        var team = await factory.Seeder.SeedTeamAsync(userId, "Sources");
        var otherOwner = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(otherOwner);
        var other = await factory.Seeder.SeedTeamAsync(otherOwner, "Other");
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.ExecuteAsync(
            "INSERT INTO riot_account_name_history (puuid, game_name, tag_line) VALUES (@puuid, 'FormerName', 'EU1')", new { puuid });

        var rosters = await ScoreboardRosterLoader.LoadAsync(conn, team, other);

        var candidates = rosters.Team1.Single().Candidates;
        candidates[0].Source.Should().Be(OcrNameSource.RiotVerified);
        candidates.Should().Contain(c => c.Name == "FormerName" && c.Source == OcrNameSource.RiotPrevious);
        candidates.Should().Contain(c => c.Source == OcrNameSource.Username);
        candidates.Select(c => c.Name.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }
}
