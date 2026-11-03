namespace Esportra.Api.Services.ScrimData;

/// <summary>A scrim of the team whose map veto finished. MySlot is the team's place in the scrim (1 or 2), which is also its side in the veto.</summary>
public sealed record VetoSessionRow(Guid SessionId, Guid VetoId, int MySlot, string? Decider, Guid? OpponentId = null);

/// <summary>One veto action. TeamSide is 'team1' or 'team2' (the veto's own team ids are random, so the side is the only link to a scrim team).</summary>
public sealed record VetoActionRow(Guid VetoId, string TeamSide, string ActionType, string? MapName, int ActionNumber, string? Side);

public sealed record VetoGameRow(Guid SessionId, int GameNumber, string? MapName, int Team1Score, int Team2Score, int? WinnerSlot, string Status);

/// <summary>What happened to one map across the team's vetoes.</summary>
public sealed record MapPickRow(string Map, int YouPicked, int YouBanned, int TheyPicked, int TheyBanned, int Decider, int Played);

/// <summary>Results on maps chosen by one side of the veto: "you", "opponent" or "decider".</summary>
public sealed record PickerResult(string PickedBy, int Games, int Wins, double? WinRatePct, int RoundsWon, int RoundsLost, double? RoundWinRatePct);

/// <summary>The side a team chose after the opponent picked a map ("attack" or "defense" as stored).</summary>
public sealed record SideChoice(string Side, int Count);

public sealed record MapPicks(
    int Vetoes, int Unattributed, IReadOnlyList<MapPickRow> Maps, IReadOnlyList<PickerResult> ByPicker, IReadOnlyList<SideChoice> YourSideChoices);
