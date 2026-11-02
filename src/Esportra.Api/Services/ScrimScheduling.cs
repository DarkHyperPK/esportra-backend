namespace Esportra.Api.Services;

/// <summary>
/// Rules for agreeing a scrim's time. A scrim is created on accept with no time; the two captains
/// then agree one through proposals. Pure so the rules are unit-tested.
/// </summary>
public static class ScrimScheduling
{
    /// <summary>A proposed time must be at least this far ahead (check-in opens 15 min before).</summary>
    public const int MinLeadMinutes = 5;
    public const int MaxLeadDays = 60;

    /// <summary>Returns an error message for an unusable proposed time, or null when it is fine.</summary>
    public static string? ValidateProposedTime(DateTime proposedAtUtc, DateTime nowUtc)
    {
        if (proposedAtUtc < nowUtc.AddMinutes(MinLeadMinutes))
            return $"Pick a time at least {MinLeadMinutes} minutes from now.";
        if (proposedAtUtc > nowUtc.AddDays(MaxLeadDays))
            return $"Pick a time within the next {MaxLeadDays} days.";
        return null;
    }

    public readonly record struct JobSchedule(DateTime Reminder, DateTime Window, DateTime Timeout);

    /// <summary>Reminder 30 min before, check-in window 15 min before, no-show timeout 15 min after.</summary>
    public static JobSchedule JobTimes(DateTime scheduledAtUtc) => new(
        scheduledAtUtc.AddMinutes(-30),
        scheduledAtUtc.AddMinutes(-15),
        scheduledAtUtc.AddMinutes(15));
}

/// <summary>What accepting an offer produces: a fixed time, or a proposal to confirm in the room.</summary>
public readonly record struct ScrimAcceptPlan(DateTime? ScheduledAt, DateTime? InitialProposalAt)
{
    /// <param name="slotId">Set when the offer booked one of the listing's published slots.</param>
    /// <param name="acceptBodyTime">A time sent with the accept request (older clients only).</param>
    /// <param name="offerProposedAt">The offering team's suggested time. The column defaults to now(), so a time
    /// that is not comfortably in the future is treated as "no suggestion".</param>
    public static ScrimAcceptPlan For(Guid? slotId, DateTime? acceptBodyTime, DateTime? offerProposedAt, DateTime nowUtc)
    {
        if (acceptBodyTime is { } explicitTime) return new(explicitTime, null);
        // The listing owner published this slot, so booking it already is the agreed time.
        if (slotId is not null && offerProposedAt is { } slotTime) return new(slotTime, null);

        var suggestion = offerProposedAt is { } p && p >= nowUtc.AddMinutes(ScrimScheduling.MinLeadMinutes) ? p : (DateTime?)null;
        return new(null, suggestion);
    }
}
