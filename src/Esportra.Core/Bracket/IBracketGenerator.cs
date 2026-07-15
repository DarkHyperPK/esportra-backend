namespace Esportra.Core.Bracket;

public interface IBracketGenerator
{
    BracketGraph Generate(
        IReadOnlyList<(Guid Id, string Name)> teams,
        Guid tournamentId,
        Guid? stageId = null,
        StageRoundConfiguration? roundConfig = null,
        int? bracketSize = null,
        int? advancementCount = null,
        BracketConfig? config = null);
}

public sealed record BracketConfig(
    string? DailyStartTime = "20:00",
    string? TournamentStartDate = null,
    int? SwissGroups = null,
    int? SwissRounds = null);
