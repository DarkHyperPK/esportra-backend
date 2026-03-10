namespace Esportra.Contracts.Requests;

public sealed record CreateReviewRequest(
    string  ReviewType,
    int     Rating,
    string? Title        = null,
    string? Comment      = null,
    string? VenueId      = null,
    string? RevieweeId   = null,
    string? TournamentId = null);

public sealed record UpdateReviewRequest(
    int?    Rating  = null,
    string? Title   = null,
    string? Comment = null);
