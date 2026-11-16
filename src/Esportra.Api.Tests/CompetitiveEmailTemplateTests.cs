using Esportra.Infrastructure.Email;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class CompetitiveEmailTemplateTests
{
    // ── LFG ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LfgInterestReceived_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.LfgInterestReceived("nitro_ace", "https://esportra.com/competitive/lfg/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void LfgInterestAccepted_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.LfgInterestAccepted("nitro_ace", "https://esportra.com/competitive/lfg/conversations/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void LfgInterestDeclined_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.LfgInterestDeclined("https://esportra.com/competitive/lfg");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void LfgListingExpiring_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.LfgListingExpiring("nitro_ace", "https://esportra.com/competitive/lfg/abc", "Mon 1 Jan 2026, 12:00 UTC");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void LfgListingExpired_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.LfgListingExpired("nitro_ace", "https://esportra.com/competitive/lfg");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void LfgSwipeMatch_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.LfgSwipeMatch("nitro_ace", "https://esportra.com/competitive/lfg/conversations/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    // ── Scrims ──────────────────────────────────────────────────────────────

    [Fact]
    public void ScrimOfferReceived_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimOfferReceived("Night Owls", "Mon 1 Jan 2026, 20:00 UTC", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimOfferAccepted_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimOfferAccepted("Crimson Five", "https://esportra.com/competitive/scrims/sessions/abc", "Mon 1 Jan 2026, 20:00 UTC");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimOfferDeclined_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimOfferDeclined("https://esportra.com/competitive/scrims");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimOfferWithdrawn_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimOfferWithdrawn("Night Owls", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimOfferExpired_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimOfferExpired("Crimson Five", "https://esportra.com/competitive/scrims");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimSessionCancelled_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimSessionCancelled("Night Owls", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimReminder_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimReminder("Crimson Five", "Mon 1 Jan 2026, 20:00 UTC", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimCheckinOpen_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimCheckinOpen("Crimson Five", "Mon 1 Jan 2026, 20:00 UTC", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimTimeoutCancelled_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimTimeoutCancelled("https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimTimeProposed_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimTimeProposed("Night Owls", "Mon 1 Jan 2026, 20:00 UTC", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimTimeClearRequested_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimTimeClearRequested("Night Owls", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimTimeAccepted_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimTimeAccepted("Crimson Five", "Mon 1 Jan 2026, 20:00 UTC", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimTimeReset_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimTimeReset("https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimResultSubmitted_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimResultSubmitted("Night Owls", "13–9", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimDisputeRaised_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimDisputeRaised("Night Owls", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }

    [Fact]
    public void ScrimDisputeResolved_ReturnsNonEmptySubjectAndHtml()
    {
        var (subject, html) = EmailTemplates.ScrimDisputeResolved("Team 1 wins", "https://esportra.com/competitive/scrims/sessions/abc");
        Assert.NotEmpty(subject);
        Assert.NotEmpty(html);
    }
}
