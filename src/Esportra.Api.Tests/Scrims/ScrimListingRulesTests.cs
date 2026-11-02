using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ScrimListingRulesTests
{
    private static readonly DateTimeOffset Until = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADaysOnlyListing_NeedsNoBookingWindowOrSlotLength()
    {
        ScrimListingRules.ValidateSchedule("recurring", null, null).Should().BeNull();
        ScrimListingRules.GeneratesSlots("recurring", null, null).Should().BeFalse();
    }

    [Fact]
    public void OlderClients_SendingBothBookingFields_StillGetSlots()
    {
        ScrimListingRules.ValidateSchedule("recurring", Until, 90).Should().BeNull();
        ScrimListingRules.GeneratesSlots("recurring", Until, 90).Should().BeTrue();
    }

    [Fact]
    public void OneBookingFieldWithoutTheOther_IsRejected()
    {
        ScrimListingRules.ValidateSchedule("recurring", Until, null).Should().Contain("together");
        ScrimListingRules.ValidateSchedule("recurring", null, 90).Should().Contain("together");
    }

    [Fact]
    public void ASlotLengthMustBePositive()
    {
        ScrimListingRules.ValidateSchedule("recurring", Until, 0).Should().Contain("positive");
        ScrimListingRules.ValidateSchedule("recurring", Until, -5).Should().Contain("positive");
    }

    [Fact]
    public void OneOffListings_KeepTheirOldRules()
    {
        ScrimListingRules.ValidateSchedule("one_off", null, null).Should().BeNull();
        ScrimListingRules.ValidateSchedule("one_off", Until, null).Should().Contain("only valid for recurring");
        ScrimListingRules.ValidateSchedule("one_off", null, 90).Should().Contain("only valid for recurring");
        ScrimListingRules.GeneratesSlots("one_off", Until, 90).Should().BeFalse();
    }

    [Fact]
    public void UnknownScheduleTypesAreRejected()
    {
        ScrimListingRules.ValidateSchedule("weekly", null, null).Should().Contain("schedule_type");
    }
}
