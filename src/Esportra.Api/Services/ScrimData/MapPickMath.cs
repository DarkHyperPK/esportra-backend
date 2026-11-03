namespace Esportra.Api.Services.ScrimData;

/// <summary>
/// Map picks across a team's scrims: what it and its opponents picked and banned, the deciders, and how maps went by who chose them.
/// A game's veto map is found the same way the Auto-fetch does it (picks in order, then the decider, shifted back by voided maps).
/// </summary>
public static class MapPickMath
{
    public const string You = "you", Opponent = "opponent", Decider = "decider";

    public static MapPicks Build(IReadOnlyList<VetoSessionRow> sessions, IReadOnlyList<VetoActionRow> actions, IReadOnlyList<VetoGameRow> games)
    {
        var rows = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        void Bump(string? map, int slot)
        {
            if (string.IsNullOrWhiteSpace(map)) return;
            var name = ValorantMaps.Resolve(map);
            if (!rows.TryGetValue(name, out var counts)) rows[name] = counts = new int[6];
            counts[slot]++;
        }
        const int YouPicked = 0, YouBanned = 1, TheyPicked = 2, TheyBanned = 3, DeciderN = 4, PlayedN = 5;

        var picker = new Dictionary<string, (int Games, int Wins, int Won, int Lost)>();
        var sides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unattributed = 0;

        foreach (var s in sessions)
        {
            var mine = $"team{s.MySlot}";
            var mineActions = actions.Where(a => a.VetoId == s.VetoId).OrderBy(a => a.ActionNumber).ToList();
            foreach (var a in mineActions)
            {
                var isMine = a.TeamSide == mine;
                if (a.ActionType == "ban") Bump(a.MapName, isMine ? YouBanned : TheyBanned);
                else if (a.ActionType == "pick") Bump(a.MapName, isMine ? YouPicked : TheyPicked);
                else if (a.ActionType == "pick_side" && isMine && !string.IsNullOrWhiteSpace(a.Side))
                    sides[a.Side!.ToLowerInvariant()] = sides.GetValueOrDefault(a.Side.ToLowerInvariant()) + 1;
            }
            Bump(s.Decider, DeciderN);

            var sessionGames = games.Where(g => g.SessionId == s.SessionId).ToList();
            var picks = mineActions.Where(a => a.ActionType == "pick" && !string.IsNullOrWhiteSpace(a.MapName))
                .Select((a, i) => new VetoMapEntry(i + 1, a.MapName!, a.TeamSide == "team1" ? 1 : 2, false)).ToList();
            if (!string.IsNullOrWhiteSpace(s.Decider) && !picks.Any(p => string.Equals(p.MapName, s.Decider, StringComparison.OrdinalIgnoreCase)))
                picks.Add(new VetoMapEntry(picks.Count + 1, s.Decider, null, true));
            var plan = new ScrimVetoMapPlan(picks, sessionGames.Where(g => g.Status == "resolved" && g.WinnerSlot is null).Select(g => g.GameNumber).ToList());

            foreach (var g in sessionGames.Where(g => g.Status == "confirmed" || (g.Status == "resolved" && g.WinnerSlot is not null)))
            {
                if (!string.IsNullOrWhiteSpace(g.MapName)) Bump(g.MapName, PlayedN);
                var entry = plan.ForGame(g.GameNumber);
                if (entry is null) { unattributed++; continue; }
                var who = entry.IsDecider ? Decider : entry.PickedBySlot == s.MySlot ? You : Opponent;
                var (mineRounds, theirRounds) = s.MySlot == 1 ? (g.Team1Score, g.Team2Score) : (g.Team2Score, g.Team1Score);
                var cur = picker.GetValueOrDefault(who);
                picker[who] = (cur.Games + 1, cur.Wins + (g.WinnerSlot == s.MySlot ? 1 : 0), cur.Won + mineRounds, cur.Lost + theirRounds);
            }
        }

        return new MapPicks(
            sessions.Count, unattributed,
            rows.Select(r => new MapPickRow(r.Key, r.Value[YouPicked], r.Value[YouBanned], r.Value[TheyPicked], r.Value[TheyBanned], r.Value[DeciderN], r.Value[PlayedN]))
                .OrderByDescending(r => r.Played).ThenByDescending(r => r.YouPicked + r.TheyPicked + r.Decider).ThenBy(r => r.Map, StringComparer.Ordinal).ToList(),
            new[] { You, Opponent, Decider }.Where(picker.ContainsKey).Select(k =>
            {
                var p = picker[k];
                return new PickerResult(k, p.Games, p.Wins, Pct(p.Wins, p.Games), p.Won, p.Lost, Pct(p.Won, p.Won + p.Lost));
            }).ToList(),
            sides.Select(kv => new SideChoice(kv.Key, kv.Value)).OrderByDescending(s => s.Count).ThenBy(s => s.Side, StringComparer.Ordinal).ToList());
    }

    private static double? Pct(int part, int whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : null;
}
