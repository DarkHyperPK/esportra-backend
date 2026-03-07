namespace Esportra.Core.Bracket;

public interface IBracketGenerator
{
    BracketGraph Generate(
        IReadOnlyList<(string Id, string Name)> teams,
        string tournamentId,
        string? stageId      = null,
        int     bestOf       = 1,
        int?    bracketSize  = null,
        int?    advancementCount = null,
        BracketConfig? config = null);
}

public sealed record BracketConfig(
    string? DailyStartTime      = "20:00",
    string? TournamentStartDate = null,
    int?    SwissGroups         = null,
    int?    SwissRounds         = null);
