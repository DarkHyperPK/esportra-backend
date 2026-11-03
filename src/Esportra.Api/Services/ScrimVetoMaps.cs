using System.Data;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>One map the veto assigned to a game: its order in the series, who picked it, and whether it was the decider.</summary>
public sealed record VetoMapEntry(int Order, string MapName, int? PickedBySlot, bool IsDecider);

/// <summary>What the map veto decided for a scrim: the maps in series order. Empty/absent when there is no completed veto.</summary>
public sealed record ScrimVetoMapPlan(IReadOnlyList<VetoMapEntry> Maps, IReadOnlyList<int> VoidedGameNumbers)
{
    /// <summary>The map game <paramref name="gameNumber"/> should be played on. A voided map is replayed, so every game number after a void shifts back by one.</summary>
    public VetoMapEntry? ForGame(int gameNumber)
    {
        if (gameNumber < 1) return null;
        var index = gameNumber - 1 - VoidedGameNumbers.Count(v => v < gameNumber);
        return index >= 0 && index < Maps.Count ? Maps[index] : null;
    }
}

public static class ScrimVetoMaps
{
    /// <summary>
    /// Whether Auto-fetch lists a game for the map the veto assigned. With no veto map, or "show all", everything is listed.
    /// A game on a map we cannot name is always listed: hiding it on a guess would lose a real game.
    /// </summary>
    public static (bool Show, bool OnVetoMap) Classify(string? expectedMap, string? playedMap, bool showAll)
    {
        var onVetoMap = expectedMap is not null && string.Equals(playedMap, expectedMap, StringComparison.OrdinalIgnoreCase);
        var hide = expectedMap is not null && !showAll && playedMap is not null && !onVetoMap;
        return (!hide, onVetoMap);
    }

    /// <summary>
    /// The veto's maps in play order: the picks in the order they were made, then the decider (the veto's selected map).
    /// Bo1 has no picks, so it is the decider alone. Null when the session has no veto or it is not finished.
    /// </summary>
    public static async Task<ScrimVetoMapPlan?> LoadAsync(IDbConnection conn, Guid sessionId)
    {
        var veto = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Status, string? SelectedMapId)?>(
            """
            SELECT v.id, v.status, v.selected_map_id
            FROM public.scrim_sessions s JOIN public.public_veto_sessions v ON v.id = s.veto_session_id
            WHERE s.id = @sessionId
            """, new { sessionId });
        if (veto is null || veto.Value.Status != "completed") return null;

        var picks = (await conn.QueryAsync<(string? Name, string TeamSide)>(
            """
            SELECT gm.map_name, a.team_side
            FROM public.public_veto_actions a
            LEFT JOIN public.game_maps gm ON gm.id::text = a.map_id
            WHERE a.session_id = @vetoId AND a.action_type = 'pick'
            ORDER BY a.action_number
            """, new { vetoId = veto.Value.Id })).ToList();
        var decider = veto.Value.SelectedMapId is null ? null : await conn.ExecuteScalarAsync<string?>(
            "SELECT map_name FROM public.game_maps WHERE id::text = @id", new { id = veto.Value.SelectedMapId });

        var maps = new List<VetoMapEntry>();
        foreach (var (name, side) in picks)
            if (!string.IsNullOrWhiteSpace(name)) maps.Add(new VetoMapEntry(maps.Count + 1, name, side == "team1" ? 1 : 2, false));
        if (!string.IsNullOrWhiteSpace(decider) && !maps.Any(m => string.Equals(m.MapName, decider, StringComparison.OrdinalIgnoreCase)))
            maps.Add(new VetoMapEntry(maps.Count + 1, decider, null, true));

        var voided = (await conn.QueryAsync<int>(
            "SELECT game_number FROM public.scrim_session_games WHERE session_id = @sessionId AND status = 'resolved' AND winner_slot IS NULL",
            new { sessionId })).ToList();
        return new ScrimVetoMapPlan(maps, voided);
    }
}
