namespace Esportra.Core.Broadcast;

public sealed record BroadcastTemplateItem(string Title, string Type, string Overlay, BroadcastSegmentPlan Plan);
public sealed record BroadcastRundownTemplate(string Id, string Name, int BestOf, BroadcastTemplateItem[] Items);

public static class BroadcastRundownTemplates
{
    public static BroadcastRundownTemplate Build(int bestOf)
    {
        if (bestOf is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(bestOf));
        var items = new List<BroadcastTemplateItem>
        {
            Item("Opening", "pre_show", "starting-soon", "match_room_opened"),
            Item("Match Introduction", "pre_show", "match-intro", "check_in_complete"),
            Item("Map picks & bans", "custom", "map-veto", "veto_step"),
        };
        for (var map = 1; map <= bestOf; map++)
        {
            items.Add(Item($"Map {map} · Agent selection", "custom", "agent-select", "agent_select", map));
            items.Add(Item($"Map {map}", "match", "ingame", "round_live", map));
            items.Add(Item($"Map {map} · Results", "post_show", "map-result", "map_end", map));
            if (map < bestOf) items.Add(Item($"Break before map {map + 1}", "break", "brb", "manual"));
        }
        items.Add(Item("Series Results", "post_show", "series", "series_decided"));
        items.Add(Item("Talent desk", "pre_show", "casters", "manual"));
        items.Add(Item("Closing", "post_show", "ending", "manual"));
        return new($"bo{bestOf}", $"Best of {bestOf}", bestOf, items.ToArray());
    }

    private static BroadcastTemplateItem Item(string title, string type, string overlay, string trigger, int? map = null) =>
        new(title, type, overlay, new(trigger, "manual", "manual", 5, [overlay], Transition: new("fade", "Fade", 300), MapNumber: map));
}
