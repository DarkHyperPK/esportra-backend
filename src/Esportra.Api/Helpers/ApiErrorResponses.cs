using System.Diagnostics;
using System.Text.RegularExpressions;
using Esportra.Api.Services;
using Npgsql;

namespace Esportra.Api.Helpers;

/// <summary>Maps unhandled exceptions to safe, user-facing API error payloads.</summary>
public static class ApiErrorResponses
{
    private static readonly Regex InternalNoise = new(
        @"\b(at |StackTrace|\.cs:line|Npgsql\.|Dapper\.|System\.)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> ConstraintMessages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tournament_invitations_redeemed_team_id_fkey"] =
                "Invite redemption could not be saved because the team link was invalid. Refresh and try again.",
            ["tournament_invitations_redeemed_participant_id_fkey"] =
                "Invite redemption could not be saved because the registration record was invalid. Refresh and try again.",
            ["chk_tournament_invitations_redeemed_fields"] =
                "Invite redemption could not be completed. Ask the organizer to resend your invitation.",
            ["ux_tournament_invitation_active_email"] =
                "This email already has an active invitation for this tournament.",
            ["uq_br_lobby_evidence_game_team"] =
                "Evidence has already been submitted for this game.",
            ["uq_br_lobby_evidence_game_participant"] =
                "Evidence has already been submitted for this game.",
        };

    public sealed record Payload(int StatusCode, string Error, string? TraceId, string? Detail);

    public static Payload FromException(Exception ex, string requestPath, bool includeDiagnostics)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..12];

        while (ex is AggregateException agg && agg.InnerException is not null)
            ex = agg.InnerException;

        switch (ex)
        {
            case GameCatalogValidationException validation:
                return new Payload(400, validation.Message, traceId, null);

            case PostgresException pg:
                return FromPostgres(pg, requestPath, traceId, includeDiagnostics);

            case UnauthorizedAccessException:
                return new Payload(403, "You don't have permission to perform this action.", traceId, null);

            case KeyNotFoundException:
                return new Payload(404, "The requested item was not found.", traceId, DetailIfAllowed(ex, includeDiagnostics));

            case ArgumentException arg when IsUserSafeMessage(arg.Message):
                return new Payload(400, arg.Message, traceId, null);

            case InvalidOperationException invalid when IsUserSafeMessage(invalid.Message):
                return new Payload(400, invalid.Message, traceId, null);

            case TimeoutException:
                return new Payload(504, "The request took too long. Please try again.", traceId, DetailIfAllowed(ex, includeDiagnostics));

            case HttpRequestException:
                return new Payload(503, "A connected service is temporarily unavailable. Try again in a moment.", traceId, DetailIfAllowed(ex, includeDiagnostics));
        }

        var area = DescribeRequestArea(requestPath);
        var fallback = area is null
            ? "We couldn't complete that request. Please refresh and try again."
            : $"We couldn't complete your {area}. Please refresh and try again.";

        return new Payload(500, fallback, traceId, DetailIfAllowed(ex, includeDiagnostics));
    }

    public static object ToJson(Payload payload, bool includeDiagnostics) =>
        includeDiagnostics && !string.IsNullOrWhiteSpace(payload.Detail)
            ? new { error = payload.Error, traceId = payload.TraceId, detail = payload.Detail }
            : string.IsNullOrWhiteSpace(payload.TraceId)
                ? new { error = payload.Error }
                : new { error = payload.Error, traceId = payload.TraceId };

    private static Payload FromPostgres(
        PostgresException pg,
        string requestPath,
        string traceId,
        bool includeDiagnostics)
    {
        if (!string.IsNullOrWhiteSpace(pg.ConstraintName)
            && ConstraintMessages.TryGetValue(pg.ConstraintName, out var constraintMessage))
        {
            var status = pg.SqlState == PostgresErrorCodes.UniqueViolation ? 409 : 400;
            return new Payload(status, constraintMessage, traceId, DetailIfAllowed(pg, includeDiagnostics));
        }

        return pg.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation =>
                new Payload(409, "This record already exists.", traceId, DetailIfAllowed(pg, includeDiagnostics)),

            PostgresErrorCodes.ForeignKeyViolation =>
                new Payload(400, ForeignKeyMessage(requestPath), traceId, DetailIfAllowed(pg, includeDiagnostics)),

            PostgresErrorCodes.CheckViolation =>
                new Payload(400, CheckViolationMessage(requestPath, pg), traceId, DetailIfAllowed(pg, includeDiagnostics)),

            PostgresErrorCodes.NotNullViolation =>
                new Payload(400, "Required information is missing. Check your entries and try again.", traceId, DetailIfAllowed(pg, includeDiagnostics)),

            PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn =>
                new Payload(503, SchemaUnavailableMessage(requestPath), traceId, DetailIfAllowed(pg, includeDiagnostics)),

            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected =>
                new Payload(409, "Another update happened at the same time. Refresh and try again.", traceId, DetailIfAllowed(pg, includeDiagnostics)),

            _ when IsUserSafeMessage(pg.MessageText) =>
                new Payload(400, pg.MessageText, traceId, DetailIfAllowed(pg, includeDiagnostics)),

            _ => new Payload(
                500,
                DescribeRequestArea(requestPath) is { } area
                    ? $"We couldn't complete your {area} due to a database error. Try again or contact support."
                    : "We couldn't save your changes due to a database error. Try again or contact support.",
                traceId,
                DetailIfAllowed(pg, includeDiagnostics)),
        };
    }

    private static string ForeignKeyMessage(string requestPath)
    {
        if (requestPath.Contains("/invitations/redeem", StringComparison.OrdinalIgnoreCase))
            return "Invite redemption failed because tournament or team data is out of date. Refresh and try again.";

        if (requestPath.Contains("/br/", StringComparison.OrdinalIgnoreCase))
            return "Battle royale data is out of date. Refresh the page and try again.";

        return "A related record is missing or was removed. Refresh the page and try again.";
    }

    private static string CheckViolationMessage(string requestPath, PostgresException pg)
    {
        if (requestPath.Contains("/invitations/", StringComparison.OrdinalIgnoreCase))
            return "This invitation cannot be used in its current state. Check expiry or ask the organizer to resend it.";

        if (!string.IsNullOrWhiteSpace(pg.ConstraintName)
            && pg.ConstraintName.Contains("invite", StringComparison.OrdinalIgnoreCase))
            return "This invitation cannot be redeemed right now. Ask the organizer to resend it.";

        return "Some details are not valid for this action. Review your input and try again.";
    }

    private static string SchemaUnavailableMessage(string requestPath)
    {
        if (requestPath.Contains("/br/", StringComparison.OrdinalIgnoreCase))
            return "Battle royale features are still updating on this server. Try again shortly.";

        return "This feature is not available on the server yet. Try again shortly or contact support.";
    }

    private static string? DescribeRequestArea(string requestPath)
    {
        if (requestPath.Contains("/invitations/redeem", StringComparison.OrdinalIgnoreCase))
            return "invite redemption";

        if (requestPath.Contains("/invitations/", StringComparison.OrdinalIgnoreCase))
            return "tournament invitation";

        if (requestPath.Contains("/br/", StringComparison.OrdinalIgnoreCase))
            return "battle royale update";

        if (requestPath.Contains("/tournaments/", StringComparison.OrdinalIgnoreCase) && requestPath.Contains("/register", StringComparison.OrdinalIgnoreCase))
            return "tournament registration";

        if (requestPath.Contains("/veto/", StringComparison.OrdinalIgnoreCase))
            return "map veto";

        if (requestPath.Contains("/teams", StringComparison.OrdinalIgnoreCase))
            return "team update";

        return null;
    }

    private static string? DetailIfAllowed(Exception ex, bool includeDiagnostics) =>
        includeDiagnostics ? ex.Message : null;

    private static bool IsUserSafeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (message.Length > 240) return false;
        if (InternalNoise.IsMatch(message)) return false;
        return true;
    }
}
