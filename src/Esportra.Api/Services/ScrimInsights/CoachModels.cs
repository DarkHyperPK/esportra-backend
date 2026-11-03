namespace Esportra.Api.Services.ScrimInsights;

/// <summary>One clutch: a player left alone against Versus enemies, and whether the round was won.</summary>
public sealed record ClutchLine(int Round, string Key, int Versus, bool Won);

/// <summary>Trade counts: a death is traded when a teammate kills the killer within 5 seconds; a trade kill is the avenging kill.</summary>
public sealed record TradeCounts(int Deaths, int Traded, int TradeKills);

public sealed record SiteRate(string Site, Rate Won);

/// <summary>Per player, from the kill timeline. Entry = took part in the round's first duel.</summary>
public sealed record CoachPlayer(
    string Key, int Deaths, int Traded, int UntradedFirstDeaths, int TradeKills, int ClutchAttempts, int ClutchWins,
    int EntryDuels, int EntryWins, int MultiKillRounds, int MultiKillRoundsWon);

/// <summary>
/// What decided rounds, worked out from who was alive when, rather than from totals: trades, leads, clutches, post-plant and retake,
/// economy conversion and tempo. Every rate is "won of rounds" so the sample is always visible.
/// </summary>
public sealed record CoachMap(
    TradeCounts Trades, Rate LedRounds, Rate TrailedRounds, IReadOnlyList<ClutchLine> Clutches,
    Rate PostPlant, IReadOnlyList<SiteRate> PostPlantBySite, Rate NoPlantOnAttack, Rate Retake, IReadOnlyList<SiteRate> RetakeBySite,
    Rate AfterPistolWin, Rate AfterPistolLoss, Rate AntiEco, Rate EcoUpsets, Rate LoadoutAhead, Rate LoadoutBehind,
    double? AvgFirstKillMs, Rate SlowRounds, IReadOnlyList<CoachPlayer> Players);
