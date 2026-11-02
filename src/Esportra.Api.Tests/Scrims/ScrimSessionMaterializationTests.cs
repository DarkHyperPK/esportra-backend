using System.Data;
using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Api.Services;
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

    // ── Every other query projection in the scrim endpoints ─────────────────────

    [Fact]
    public void CheckIn_SessionLookup_CanBeBuilt_FromTheColumnsCheckInAsyncReturns()
    {
        // timestamptz columns arrive as DateTime; a DateTimeOffset constructor parameter cannot bind to them.
        (string, Type)[] columns =
        [
            ("team1_id", typeof(Guid)), ("team2_id", typeof(Guid)), ("status", typeof(string)),
            ("team1_checked_in_at", typeof(DateTime)), ("team2_checked_in_at", typeof(DateTime)),
            ("checkin_window_opened_at", typeof(DateTime)), ("game_slug", typeof(string)),
            ("team1_name", typeof(string)), ("team2_name", typeof(string)),
        ];
        var type = typeof(ScrimEndpoints).GetNestedType("ScrimSessionCheckinRef", System.Reflection.BindingFlags.NonPublic)!;
        foreach (var nulls in new[] { false, true })
        {
            var reader = Reader(columns, nulls);
            reader.Read();
            var act = () => reader.GetRowParser(type)(reader);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void EveryQueryProjection_IsMappedByName_NotByConstructorPosition()
    {
        // A positional record only binds when the query returns exactly its columns, in order, with
        // exact types. That has taken the scrim room and check-in down. Row types must have a
        // parameterless constructor so Dapper maps by column name. Request DTOs are exempt.
        var offenders = new List<string>();
        foreach (var host in new[] { typeof(ScrimEndpoints), typeof(ScrimScheduleEndpoints) })
        {
            foreach (var nested in host.GetNestedTypes(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            {
                if (nested.Name.EndsWith("Request", StringComparison.Ordinal) || nested.Name.StartsWith('<')) continue;
                if (nested.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes) is null)
                    offenders.Add($"{host.Name}.{nested.Name}");
            }
        }
        offenders.Should().BeEmpty("these row types would break when a column is added or reordered");
    }
}
