namespace Esportra.Api.Services;

/// <summary>Validation of a listing's schedule fields.</summary>
public static class ScrimListingRules
{
    /// <summary>
    /// A listing is a set of days a team usually plays; the exact time of each scrim is agreed in its room.
    /// Bookable slot generation is optional and only used when BOTH a booking window and a slot length are given
    /// (older clients). Returns an error message, or null when the schedule is acceptable.
    /// </summary>
    public static string? ValidateSchedule(string scheduleType, DateTimeOffset? scheduleUntil, int? slotDurationMinutes)
    {
        if (scheduleType is not ("one_off" or "recurring"))
            return "schedule_type must be 'one_off' or 'recurring'.";

        if (scheduleType == "recurring")
        {
            if (slotDurationMinutes is <= 0) return "slot_duration_minutes must be positive.";
            if ((scheduleUntil is null) != (slotDurationMinutes is null))
                return "schedule_until and slot_duration_minutes must be given together, or both left out.";
            return null;
        }

        if (scheduleUntil is not null) return "schedule_until is only valid for recurring listings.";
        if (slotDurationMinutes is not null) return "slot_duration_minutes is only valid for recurring listings.";
        return null;
    }

    public static bool GeneratesSlots(string scheduleType, DateTimeOffset? scheduleUntil, int? slotDurationMinutes) =>
        scheduleType == "recurring" && scheduleUntil is not null && slotDurationMinutes is > 0;
}
