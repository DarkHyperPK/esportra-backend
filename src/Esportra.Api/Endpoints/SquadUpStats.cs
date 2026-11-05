using System.Data;
using Dapper;

namespace Esportra.Api.Endpoints;

internal enum SquadUpCounter { SessionsStarted, SwipesRight, SwipesLeft, Matches }

/// <summary>Durable per-day, per-region counters that back the Admin Centre (live rows are ephemeral).</summary>
internal static class SquadUpStats
{
    // Column names come from this closed switch, never from input.
    private static string Column(SquadUpCounter counter) => counter switch
    {
        SquadUpCounter.SessionsStarted => "sessions_started",
        SquadUpCounter.SwipesRight => "swipes_right",
        SquadUpCounter.SwipesLeft => "swipes_left",
        SquadUpCounter.Matches => "matches",
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    public static Task BumpAsync(IDbConnection conn, string region, SquadUpCounter counter, IDbTransaction? tx = null)
    {
        var column = Column(counter);
        return conn.ExecuteAsync(
            $"""
            INSERT INTO public.squad_up_daily_stats (day, region, {column})
            VALUES ((now() AT TIME ZONE 'utc')::date, @region, 1)
            ON CONFLICT (day, region) DO UPDATE SET {column} = public.squad_up_daily_stats.{column} + 1
            """, new { region }, tx);
    }
}
