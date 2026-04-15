using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Stub endpoints for tournament–sponsor linking.
/// The tournament_sponsors table was dropped (20260407160644);
/// these return empty results for backward compatibility.
/// </summary>
public static class TournamentSponsorEndpoints
{
    public static void MapTournamentSponsorEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tournaments/{tournamentId}/sponsors", (Guid tournamentId) =>
            Results.Ok(Array.Empty<object>()));
    }
}
