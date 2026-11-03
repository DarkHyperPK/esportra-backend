using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimVetoAbandonTests(ApiFactory factory)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(20);

    private sealed record Seed(Guid SessionId, Guid VetoId);

    /// <summary>An in-progress scrim whose veto was last touched <paramref name="idleMinutes"/> ago.</summary>
    private async Task<Seed> SeedAsync(string vetoStatus, double idleMinutes)
    {
        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(owner1);
        await factory.Seeder.SeedAuthUserAsync(owner2);
        var team1 = await factory.Seeder.SeedTeamAsync(owner1, "Alpha " + Guid.NewGuid().ToString("N")[..6]);
        var team2 = await factory.Seeder.SeedTeamAsync(owner2, "Bravo " + Guid.NewGuid().ToString("N")[..6]);
        await using var conn = factory.Seeder.OpenConnection();
        var veto = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.public_veto_sessions (game, best_of, team1_name, team2_name, status, host_token, team1_token, team2_token, updated_at)
            VALUES ('valorant', 3, 'A', 'B', @vetoStatus, @h, @t1, @t2, now() - (@idle * interval '1 minute')) RETURNING id
            """,
            new { vetoStatus, h = Guid.NewGuid().ToString("N"), t1 = Guid.NewGuid().ToString("N"), t2 = Guid.NewGuid().ToString("N"), idle = idleMinutes });
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status, veto_session_id) VALUES (@team1, @team2, 'valorant', 'in_progress', @veto) RETURNING id",
            new { team1, team2, veto });
        return new Seed(session, veto);
    }

    private async Task<(string Status, string? Reason)> SessionAsync(Guid id)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await conn.QuerySingleAsync<(string, string?)>("SELECT status::text, cancelled_reason FROM public.scrim_sessions WHERE id = @id", new { id });
    }

    private async Task<VetoAbandonOutcome> RunAsync(Seed s)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await ScrimVetoAbandon.EvaluateAsync(conn, s.SessionId, s.VetoId, Window);
    }

    [Fact]
    public async Task A_completed_veto_never_cancels_the_scrim()
    {
        var s = await SeedAsync("completed", idleMinutes: 90);
        (await RunAsync(s)).Kind.Should().Be(VetoAbandonKind.Skipped);
        (await SessionAsync(s.SessionId)).Status.Should().Be("in_progress");
    }

    [Fact]
    public async Task A_veto_still_being_acted_on_is_kept_and_checked_again_for_the_rest_of_the_window()
    {
        var s = await SeedAsync("in_progress", idleMinutes: 12);
        var outcome = await RunAsync(s);
        outcome.Kind.Should().Be(VetoAbandonKind.StillActive);
        outcome.Remaining.Should().BeCloseTo(TimeSpan.FromMinutes(8), TimeSpan.FromSeconds(30));
        (await SessionAsync(s.SessionId)).Status.Should().Be("in_progress");
    }

    [Fact]
    public async Task A_veto_nobody_touched_for_the_whole_window_cancels_with_its_own_reason()
    {
        var s = await SeedAsync("in_progress", idleMinutes: 25);
        var outcome = await RunAsync(s);
        outcome.Kind.Should().Be(VetoAbandonKind.Cancelled);
        (await SessionAsync(s.SessionId)).Should().Be(("cancelled", "veto_abandoned"));
    }

    [Fact]
    public async Task A_veto_that_moves_between_the_check_and_the_cancel_is_not_cancelled()
    {
        var s = await SeedAsync("in_progress", idleMinutes: 25);
        // The same guard the job relies on: activity inside the window blocks the UPDATE even if the earlier read said "idle".
        await using (var conn = factory.Seeder.OpenConnection())
            await conn.ExecuteAsync("UPDATE public.public_veto_sessions SET updated_at = now() WHERE id = @id", new { id = s.VetoId });
        (await RunAsync(s)).Kind.Should().Be(VetoAbandonKind.StillActive);
        (await SessionAsync(s.SessionId)).Status.Should().Be("in_progress");
    }

    [Fact]
    public async Task A_scrim_that_moved_on_is_left_alone()
    {
        var s = await SeedAsync("in_progress", idleMinutes: 25);
        await using (var conn = factory.Seeder.OpenConnection())
            await conn.ExecuteAsync("UPDATE public.scrim_sessions SET status = 'completed' WHERE id = @id", new { id = s.SessionId });
        (await RunAsync(s)).Kind.Should().Be(VetoAbandonKind.Skipped);
        (await SessionAsync(s.SessionId)).Status.Should().Be("completed");
    }
}
