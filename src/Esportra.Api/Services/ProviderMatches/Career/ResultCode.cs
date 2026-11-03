namespace Esportra.Api.Services.ProviderMatches.Career;

/// <summary>The outcome of a map for a team, from the rounds each side won.</summary>
public static class ResultCode
{
    public const string Win = "win";
    public const string Loss = "loss";
    public const string Draw = "draw";

    public static string Of(int? won, int? lost) => (won, lost) switch
    {
        (null, _) or (_, null) => Draw,
        var (w, l) when w > l => Win,
        var (w, l) when w < l => Loss,
        _ => Draw,
    };
}
