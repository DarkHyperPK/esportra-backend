namespace Esportra.Core.Broadcast;

public sealed record BroadcastSegmentTransition(string Mode, string? Name = null, int? DurationMs = null);
public sealed record BroadcastSegmentPlan(string Trigger, string Mode, string HoldMode, int HoldSeconds,
    string[] Graphics, string? Scene = null, BroadcastSegmentTransition? Transition = null, int? MapNumber = null);

public static class BroadcastSegmentPlans
{
    public static readonly string[] Triggers = ["manual", "match_room_opened", "check_in_complete", "veto_step", "veto_complete",
        "agent_select", "round_live", "round_end", "kill", "multi_kill", "spike_planted", "spike_defused", "map_end",
        "series_decided", "rescheduled", "forfeit", "clock_time", "delay", "hotkey", "remote_command", "observer_flag"];
    public static readonly string[] AutomaticTriggers = ["round_live", "round_end", "spike_planted", "spike_defused", "map_end", "observer_flag"];

    public static string? Validate(BroadcastSegmentPlan plan)
    {
        if (!Triggers.Contains(plan.Trigger) || plan.Mode is not ("manual" or "automatic")) return "Choose a supported trigger and mode.";
        if (plan.Mode == "automatic" && !AutomaticTriggers.Contains(plan.Trigger)) return "This trigger is a manual cue until its live delivery is available.";
        if (plan.HoldMode is not ("manual" or "window" or "immediate") || plan.HoldSeconds is < 0 or > 60 || plan.HoldMode == "window" && plan.HoldSeconds < 1)
            return "Choose Manual, immediate, or a 1–60 second hold window.";
        if (plan.Graphics is null || plan.Graphics.Length > 1 || plan.Graphics.Any(name => string.IsNullOrEmpty(name) || name.Length > 40 || name.Any(c => !char.IsAsciiLetterLower(c) && c != '-')))
            return "Choose one existing screen for this item.";
        if (plan.Scene is not null && (string.IsNullOrWhiteSpace(plan.Scene) || plan.Scene.Length > 256 || plan.Scene.Any(char.IsControl))) return "Choose a valid OBS scene name.";
        if (plan.MapNumber is < 1 or > 9) return "Map number must be 1–9.";
        var transition = plan.Transition;
        if (transition is not null && (transition.Mode is not ("current" or "cut" or "fade" or "stinger") || transition.Mode != "current" &&
            (string.IsNullOrWhiteSpace(transition.Name) || transition.Name.Length > 256 || transition.Name.Any(char.IsControl)) ||
            transition.Mode == "fade" && transition.DurationMs is not (>= 50 and <= 10000))) return "Use the current OBS transition or a named Cut, Fade or Stinger.";
        return null;
    }
}
