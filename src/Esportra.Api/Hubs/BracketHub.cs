using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time bracket updates — replaces useBracketRealtime + useStageRealtime Supabase subscriptions.
/// Groups: bracket:{versionId}, stage:{tournamentId}
/// </summary>
[Authorize]
public sealed class BracketHub(IDbConnectionFactory db, ILogger<BracketHub> logger) : Hub
{
    public async Task JoinBracket(string versionId)
    {
        if (!await HubAuthHelper.EnsureBracketVersionAccessAsync(Context, db, versionId))
            throw new HubException("You don't have access to this bracket.");

        await Groups.AddToGroupAsync(Context.ConnectionId, BracketGroup(versionId));
        logger.LogDebug("Client {Conn} joined bracket:{VersionId}", Context.ConnectionId, versionId);
    }

    public async Task LeaveBracket(string versionId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, BracketGroup(versionId));

    public async Task JoinTournament(string tournamentId)
    {
        if (!await HubAuthHelper.EnsureTournamentSubscriptionAccessAsync(Context, db, tournamentId))
            throw new HubException("You don't have access to this tournament feed.");

        await Groups.AddToGroupAsync(Context.ConnectionId, TournamentGroup(tournamentId));
        logger.LogDebug("Client {Conn} joined tournament:{TournamentId}", Context.ConnectionId, tournamentId);
    }

    public async Task LeaveTournament(string tournamentId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TournamentGroup(tournamentId));

    public static string BracketGroup(string versionId)    => $"bracket:{versionId}";
    public static string TournamentGroup(string tournamentId) => $"tournament:{tournamentId}";
}

/// <summary>
/// Strongly-typed events broadcast to bracket group clients.
/// Call via IHubContext&lt;BracketHub&gt; from endpoints/services.
/// </summary>
public static class BracketHubEvents
{
    /// <summary>A brkt_matches row was updated (score, status, winner, etc.).</summary>
    public const string MatchUpdated = "MatchUpdated";

    /// <summary>A new brkt_matches row was inserted (Swiss next round, BYE advance).</summary>
    public const string MatchInserted = "MatchInserted";

    /// <summary>The bracket stage has completed — champion determined.</summary>
    public const string StageCompleted = "StageCompleted";

    /// <summary>Bracket was reset to round 0.</summary>
    public const string BracketReset = "BracketReset";

    /// <summary>A tournament_stages row changed status.</summary>
    public const string StageUpdated = "StageUpdated";

    /// <summary>A new brkt_versions row was created.</summary>
    public const string VersionCreated = "VersionCreated";

    /// <summary>One or more brkt_matches rows were deleted (e.g. Swiss round deletion).</summary>
    public const string MatchDeleted = "MatchDeleted";
}
