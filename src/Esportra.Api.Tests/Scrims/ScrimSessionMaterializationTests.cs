using System.Data;
using Dapper;
using Esportra.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

/// <summary>
/// GetSessionAsync runs "SELECT ss.*, t1.name AS team1_name, ... my_role". If the session type cannot
/// be built from those columns, the scrim room returns 500. A fake reader with the real column order
/// reproduces that without a database.
/// </summary>
public sealed class ScrimSessionMaterializationTests
{
    static ScrimSessionMaterializationTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    // scrim_sessions column order after all migrations, then the query's joined columns.
    private static readonly (string Name, Type Type)[] QueryColumns =
    [
        ("id", typeof(Guid)), ("team1_id", typeof(Guid)), ("team2_id", typeof(Guid)),
        ("game_slug", typeof(string)), ("mode_key", typeof(string)), ("scheduled_at", typeof(DateTime)),
        ("duration_minutes", typeof(int)), ("server_info", typeof(string)), ("lobby_code", typeof(string)),
        ("status", typeof(string)), ("result_reported_by_team1", typeof(bool)), ("result_reported_by_team2", typeof(bool)),
        ("team1_score", typeof(int)), ("team2_score", typeof(int)), ("created_at", typeof(DateTime)), ("updated_at", typeof(DateTime)),
        ("team1_checked_in_at", typeof(DateTime)), ("team2_checked_in_at", typeof(DateTime)),
        ("hangfire_job_ids", typeof(string)), ("checkin_window_opened_at", typeof(DateTime)),
        ("veto_session_id", typeof(Guid)), ("veto_team1_token", typeof(string)), ("veto_team2_token", typeof(string)),
        ("best_of", typeof(int)), ("cancelled_reason", typeof(string)), ("cancelled_by_team_id", typeof(Guid)),
        ("team1_name", typeof(string)), ("team1_logo", typeof(string)),
        ("team2_name", typeof(string)), ("team2_logo", typeof(string)), ("my_role", typeof(string)),
    ];

    private static IDataReader Reader((string Name, Type Type)[] columns, bool nulls)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns) table.Columns.Add(name, type);
        var row = table.NewRow();
        foreach (var (name, type) in columns)
        {
            if (nulls && type != typeof(Guid) && name is not ("id" or "team1_id" or "team2_id" or "status" or "created_at" or "updated_at"
                or "result_reported_by_team1" or "result_reported_by_team2" or "best_of"))
            {
                row[name] = DBNull.Value;
                continue;
            }
            row[name] = type == typeof(Guid) ? Guid.NewGuid()
                : type == typeof(string) ? (name == "status" ? "cancelled" : "x")
                : type == typeof(DateTime) ? DateTime.UtcNow
                : type == typeof(bool) ? false
                : 1;
        }
        table.Rows.Add(row);
        return table.CreateDataReader();
    }

    private static Type SessionType() =>
        typeof(ScrimEndpoints).GetNestedType("ScrimSessionWithTokens", System.Reflection.BindingFlags.NonPublic)!;

    private static object Parse(IDataReader reader)
    {
        reader.Read();
        return reader.GetRowParser(SessionType())(reader);
    }

    [Fact]
    public void Session_CanBeBuilt_FromTheColumnsGetSessionAsyncReturns()
    {
        var act = () => Parse(Reader(QueryColumns, nulls: false));
        act.Should().NotThrow();
    }

    [Fact]
    public void Session_CanBeBuilt_WhenOptionalColumnsAreNull()
    {
        var act = () => Parse(Reader(QueryColumns, nulls: true));
        act.Should().NotThrow();
    }

    [Fact]
    public void Session_DoesNotDependOnColumnOrder()
    {
        var shuffled = QueryColumns.Reverse().ToArray();
        var act = () => Parse(Reader(shuffled, nulls: false));
        act.Should().NotThrow("a future ALTER TABLE ADD COLUMN must not take the scrim room down");
    }

    [Fact]
    public void Session_CarriesTheCancelFields()
    {
        var session = Parse(Reader(QueryColumns, nulls: false));
        SessionType().GetProperty("CancelledReason")!.GetValue(session).Should().Be("x");
        SessionType().GetProperty("CancelledByTeamId")!.GetValue(session).Should().NotBeNull();
    }
}
