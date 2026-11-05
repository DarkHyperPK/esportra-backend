using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>Moderation notifications for Squad Up reports. Wording never names the reporter.</summary>
internal static class SquadUpModeration
{
    private const string Link = "/competitive/lfg/squad-up";

    private static readonly Dictionary<string, string> ReasonLabels = new()
    {
        ["harassment"] = "harassment",
        ["hate_speech"] = "hate speech",
        ["cheating"] = "cheating",
        ["fake_rank"] = "a misleading rank",
        ["spam_scam"] = "spam or scams",
        ["inappropriate_profile"] = "an inappropriate profile",
        ["other"] = "a rules violation",
    };

    public static string ReasonLabel(string reason) => ReasonLabels.GetValueOrDefault(reason, "a rules violation");

    public static Task<SwipePush> ReporterAsync(IDbConnection conn, IDbTransaction tx, Guid reporterId, Guid reportId, bool actionTaken) =>
        InsertAsync(conn, tx, reporterId, "squad_up_report_resolved", "We reviewed your report",
            actionTaken
                ? "Thank you for reporting. Our team reviewed it and took action."
                : "Thank you for reporting. Our team reviewed it and did not find a violation of the rules.",
            new { report_id = reportId });

    public static Task<SwipePush> WarningAsync(IDbConnection conn, IDbTransaction tx, Guid userId, Guid reportId, string reason) =>
        InsertAsync(conn, tx, userId, "squad_up_warning", "Warning from the Squad Up team",
            $"We received a report about {ReasonLabel(reason)} and it was upheld. Please follow the community rules; repeated reports can restrict your access to Squad Up.",
            new { report_id = reportId });

    public static Task<SwipePush> RestrictedAsync(IDbConnection conn, IDbTransaction tx, Guid userId, Guid reportId, string reason, DateTime? until) =>
        InsertAsync(conn, tx, userId, "squad_up_restricted", "Squad Up access restricted",
            $"You can't use Squad Up {(until is null ? "permanently" : $"until {until:yyyy-MM-dd HH:mm} UTC")} because of a report about {ReasonLabel(reason)}.",
            new { report_id = reportId, until });

    private static async Task<SwipePush> InsertAsync(
        IDbConnection conn, IDbTransaction tx, Guid userId, string type, string title, string message, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var id = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
            VALUES (@userId, @type::notification_type, @title, @message, @link, @json::jsonb, FALSE)
            RETURNING id
            """, new { userId, type, title, message, link = Link, json }, tx);
        return new SwipePush(userId, new { id, type, title, message, link = Link, data = JsonSerializer.Deserialize<JsonElement>(json) });
    }

    public static Task PushAsync(IHubContext<NotificationHub> hub, IEnumerable<SwipePush> pushes) =>
        LfgSwipeMatchmaker.PushAsync(hub, pushes);
}
