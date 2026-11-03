using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimVetoMapsLoadTests(ApiFactory factory)
{
    private async Task<Guid> MapAsync(System.Data.IDbConnection conn, string name) =>
        await conn.QuerySingleAsync<Guid>("INSERT INTO public.game_maps (game, map_name) VALUES ('valorant', @name) RETURNING id", new { name = name + "-" + Guid.NewGuid().ToString("N")[..6] });

    private async Task<(Guid Session, Guid Veto, string[] Names)> SeedAsync(string vetoStatus, bool bo1)
    {
        await using var conn = factory.Seeder.OpenConnection();
        var o1 = Guid.NewGuid(); var o2 = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(o1); await factory.Seeder.SeedAuthUserAsync(o2);
        var t1 = await factory.Seeder.SeedTeamAsync(o1, "Alpha " + Guid.NewGuid().ToString("N")[..6]);
        var t2 = await factory.Seeder.SeedTeamAsync(o2, "Bravo " + Guid.NewGuid().ToString("N")[..6]);
        var lotus = await MapAsync(conn, "Lotus"); var bind = await MapAsync(conn, "Bind"); var haven = await MapAsync(conn, "Haven");
        var decider = bo1 ? lotus : haven;
        var veto = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.public_veto_sessions (game, best_of, team1_name, team2_name, status, host_token, team1_token, team2_token, selected_map_id)
            VALUES ('valorant', @bo, 'A', 'B', @status, @h, @a, @b, @decider) RETURNING id
            """,
            new { bo = bo1 ? 1 : 3, status = vetoStatus, h = Guid.NewGuid().ToString("N"), a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N"), decider = decider.ToString() });
        if (!bo1)
        {
            await conn.ExecuteAsync("INSERT INTO public.public_veto_actions (session_id, team_id, team_side, action_type, map_id, action_number) VALUES (@v, gen_random_uuid(), 'team1', 'pick', @m, 5)", new { v = veto, m = lotus.ToString() });
            await conn.ExecuteAsync("INSERT INTO public.public_veto_actions (session_id, team_id, team_side, action_type, map_id, action_number) VALUES (@v, gen_random_uuid(), 'team2', 'pick', @m, 6)", new { v = veto, m = bind.ToString() });
            await conn.ExecuteAsync("INSERT INTO public.public_veto_actions (session_id, team_id, team_side, action_type, map_id, action_number) VALUES (@v, gen_random_uuid(), 'team1', 'ban', @m, 1)", new { v = veto, m = haven.ToString() });
        }
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status, veto_session_id, best_of) VALUES (@t1, @t2, 'valorant', 'in_progress', @veto, @bo) RETURNING id",
            new { t1, t2, veto, bo = bo1 ? 1 : 3 });
        var names = await conn.QueryAsync<string>("SELECT map_name FROM public.game_maps WHERE id = ANY(@ids) ORDER BY array_position(@ids, id)", new { ids = new[] { lotus, bind, haven } });
        return (session, veto, names.ToArray());
    }

    [Fact]
    public async Task A_bo3_veto_gives_picks_in_order_then_the_decider()
    {
        var s = await SeedAsync("completed", bo1: false);
        await using var conn = factory.Seeder.OpenConnection();
        var plan = await ScrimVetoMaps.LoadAsync(conn, s.Session);
        plan!.Maps.Select(m => m.MapName).Should().Equal(s.Names[0], s.Names[1], s.Names[2]);
        plan.Maps.Select(m => m.PickedBySlot).Should().Equal(1, 2, null);
        plan.Maps[2].IsDecider.Should().BeTrue();
    }

    [Fact]
    public async Task A_bo1_veto_is_just_the_decider()
    {
        var s = await SeedAsync("completed", bo1: true);
        await using var conn = factory.Seeder.OpenConnection();
        var plan = await ScrimVetoMaps.LoadAsync(conn, s.Session);
        plan!.Maps.Should().ContainSingle().Which.MapName.Should().Be(s.Names[0]);
    }

    [Fact]
    public async Task An_unfinished_veto_gives_no_plan()
    {
        var s = await SeedAsync("in_progress", bo1: false);
        await using var conn = factory.Seeder.OpenConnection();
        (await ScrimVetoMaps.LoadAsync(conn, s.Session)).Should().BeNull();
    }
}
