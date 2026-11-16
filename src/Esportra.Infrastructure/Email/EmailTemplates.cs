using System.Net;
using System.Text;

namespace Esportra.Infrastructure.Email;

/// <summary>
/// Produces branded HTML email bodies per the Esportra Brand Identity Guide.
/// Matte black surfaces, rose sole accent, sharp corners, spec-sheet typography,
/// no emoji/hype voice. Reference: frontend docs/UI_DESIGN_GUIDE.md.
/// </summary>
public static class EmailTemplates
{
    // ── LFG templates ─────────────────────────────────────────────────────────

    public static (string Subject, string Html) LfgInterestReceived(string fromUsername, string listingUrl) =>
    (
        $"Someone is interested in your team listing",
        Wrap($"{E(fromUsername)} wants to join your squad", "New Interest", $"""
            {Eyebrow("Find teammates")}
            {H1("Someone wants in")}
            {P($"<strong style='color:{Headline};'>{E(fromUsername)}</strong> has expressed interest in joining your team. Review their profile and decide whether to accept or decline.")}
            {Btn(listingUrl, "View Interest")}
            {P($"<span style='color:{SubtleText};font-size:13px;'>You can manage all interests from your listing page.</span>")}
        """)
    );

    public static (string Subject, string Html) LfgInterestAccepted(string ownerUsername, string conversationUrl) =>
    (
        "Your team interest was accepted",
        Wrap($"{E(ownerUsername)} accepted your interest", "Interest Accepted", $"""
            {Eyebrow("Find teammates")}
            {H1("You're in.")}
            {P($"<strong style='color:{Headline};'>{E(ownerUsername)}</strong> accepted your interest. Head over to coordinate next steps.")}
            {Btn(conversationUrl, "Open Listing")}
        """)
    );

    public static (string Subject, string Html) LfgInterestDeclined(string discoveryUrl) =>
    (
        "Your team interest was declined",
        Wrap("Your interest was declined", "Interest Declined", $"""
            {Eyebrow("Find teammates")}
            {H1("Not this time")}
            {P("The team decided not to move forward with your interest. Keep looking — there are plenty of teams out there.")}
            {Btn(discoveryUrl, "Browse Listings")}
        """)
    );

    public static (string Subject, string Html) LfgListingExpiring(string username, string listingUrl, string expiresAt) =>
    (
        "Your team listing expires soon",
        Wrap("Your listing expires in 24 hours", "Listing Expiring", $"""
            {Eyebrow("Find teammates")}
            {H1("Your listing expires soon")}
            {P($"Hi {E(username)}, your team listing will expire in approximately 24 hours. Renew it now to keep receiving interest from players.")}
            {SpecOpen()}
              {InfoRow("Expires at", E(expiresAt), isLast: true)}
            {SpecClose}
            {Btn(listingUrl, "Renew Listing")}
        """)
    );

    public static (string Subject, string Html) LfgListingExpired(string username, string discoveryUrl) =>
    (
        "Your team listing has expired",
        Wrap("Your listing has expired", "Listing Expired", $"""
            {Eyebrow("Find teammates")}
            {H1("Your listing expired")}
            {P($"Hi {E(username)}, your team listing has expired and is no longer visible to players. Create a new listing to start receiving interest again.")}
            {Btn(discoveryUrl, "Create New Listing")}
        """)
    );

    public static (string Subject, string Html) LfgSwipeMatch(string matchedUsername, string matchUrl) =>
    (
        $"You matched with {E(matchedUsername)}",
        Wrap($"Squad Up match with {E(matchedUsername)}", "New Match", $"""
            {Eyebrow("Squad up")}
            {H1("It's a match.")}
            {P($"You and <strong style='color:{Headline};'>{E(matchedUsername)}</strong> both swiped right. Start a conversation and form your squad.")}
            {Btn(matchUrl, "Connect Now")}
        """)
    );

    // ── Scrim templates ───────────────────────────────────────────────────────

    public static (string Subject, string Html) ScrimOfferReceived(string offeringTeamName, string proposedAt, string workspaceUrl) =>
    (
        $"{E(offeringTeamName)} wants to scrim",
        Wrap($"Scrim offer from {E(offeringTeamName)}", "Scrim Offer", $"""
            {Eyebrow("Find scrims")}
            {H1("New scrim offer")}
            {P($"<strong style='color:{Headline};'>{E(offeringTeamName)}</strong> has sent you a scrim offer. Review and accept or decline from your workspace.")}
            {SpecOpen()}
              {InfoRow("From", E(offeringTeamName))}
              {InfoRow("Proposed time", string.IsNullOrWhiteSpace(proposedAt) ? "Flexible" : E(proposedAt), isLast: true)}
            {SpecClose}
            {Btn(workspaceUrl, "Review Offer")}
        """)
    );

    public static (string Subject, string Html) ScrimOfferAccepted(string listingTeamName, string sessionUrl, string? scheduledAt) =>
    (
        $"{E(listingTeamName)} accepted your scrim offer",
        Wrap($"Scrim confirmed with {E(listingTeamName)}", "Offer Accepted", $"""
            {Eyebrow("Find scrims")}
            {H1("Scrim confirmed.")}
            {P($"<strong style='color:{Headline};'>{E(listingTeamName)}</strong> accepted your offer. Your scrim session is now scheduled.")}
            {(string.IsNullOrWhiteSpace(scheduledAt) ? "" : $"""
            {SpecOpen()}
              {InfoRow("Scheduled", E(scheduledAt), isLast: true)}
            {SpecClose}
            """)}
            {Btn(sessionUrl, "Open Session")}
        """)
    );

    public static (string Subject, string Html) ScrimOfferDeclined(string discoveryUrl) =>
    (
        "Your scrim offer was declined",
        Wrap("Scrim offer declined", "Offer Declined", $"""
            {Eyebrow("Find scrims")}
            {H1("Offer declined")}
            {P("The team decided not to proceed with your scrim offer. Browse other listings to find your next match-up.")}
            {Btn(discoveryUrl, "Browse Listings")}
        """)
    );

    public static (string Subject, string Html) ScrimOfferWithdrawn(string offeringTeamName, string workspaceUrl) =>
    (
        $"{E(offeringTeamName)} withdrew their scrim offer",
        Wrap($"Offer withdrawn by {E(offeringTeamName)}", "Offer Withdrawn", $"""
            {Eyebrow("Find scrims")}
            {H1("Offer withdrawn")}
            {P($"<strong style='color:{Headline};'>{E(offeringTeamName)}</strong> withdrew their scrim offer. Your listing is still active and open to new offers.")}
            {Btn(workspaceUrl, "View Workspace")}
        """)
    );

    public static (string Subject, string Html) ScrimOfferExpired(string listingTeamName, string discoveryUrl) =>
    (
        "Your scrim offer expired",
        Wrap("Scrim offer expired", "Offer Expired", $"""
            {Eyebrow("Find scrims")}
            {H1("Offer expired")}
            {P($"Your scrim offer to <strong style='color:{Headline};'>{E(listingTeamName)}</strong> expired after 48 hours without a response. Browse listings to send a new offer.")}
            {Btn(discoveryUrl, "Browse Listings")}
        """)
    );

    public static (string Subject, string Html) ScrimSessionCancelled(string cancellerName, string workspaceUrl) =>
    (
        "Your scrim session was cancelled",
        Wrap($"Session cancelled by {E(cancellerName)}", "Session Cancelled", $"""
            {Eyebrow("Find scrims")}
            {H1("Session cancelled")}
            {P($"<strong style='color:{Headline};'>{E(cancellerName)}</strong> cancelled the upcoming scrim session. You can find a new opponent from the listing board.")}
            {Btn(workspaceUrl, "Find New Opponent")}
        """)
    );

    public static (string Subject, string Html) ScrimReminder(string opponentName, string scheduledAt, string sessionUrl) =>
    (
        "Scrim in 30 minutes",
        Wrap($"Scrim vs {E(opponentName)} starts soon", "30-Minute Reminder", $"""
            {Eyebrow("Scrim reminder")}
            {H1("30 minutes to go")}
            {P($"Your scrim against <strong style='color:{Headline};'>{E(opponentName)}</strong> starts in 30 minutes. Make sure your team is ready and check in on time.")}
            {SpecOpen()}
              {InfoRow("Opponent", E(opponentName))}
              {InfoRow("Scheduled", E(scheduledAt), isLast: true)}
            {SpecClose}
            {Btn(sessionUrl, "Open Session")}
        """)
    );

    public static (string Subject, string Html) ScrimCheckinOpen(string opponentName, string scheduledAt, string sessionUrl) =>
    (
        "Check in now — scrim starts in 15 minutes",
        Wrap($"Check in for your scrim vs {E(opponentName)}", "Check-in Open", $"""
            {Eyebrow("Check-in open")}
            {H1("Check in now")}
            {P($"The check-in window is open for your scrim against <strong style='color:{Headline};'>{E(opponentName)}</strong>. Both teams must check in before the session starts.")}
            {SpecOpen()}
              {InfoRow("Opponent", E(opponentName))}
              {InfoRow("Scheduled", E(scheduledAt), isLast: true)}
            {SpecClose}
            {Btn(sessionUrl, "Check In")}
            {P($"<span style='color:{SubtleText};font-size:13px;'>Failing to check in within 15 minutes may result in automatic cancellation.</span>")}
        """)
    );

    public static (string Subject, string Html) ScrimTimeoutCancelled(string sessionUrl) =>
    (
        "Scrim session cancelled — check-in timeout",
        Wrap("Session cancelled due to no check-in", "Session Cancelled", $"""
            {Eyebrow("Find scrims")}
            {H1("Session cancelled")}
            {P("One or both teams did not check in within the required window. The session has been automatically cancelled.")}
            {Btn(sessionUrl, "Find New Opponent")}
        """)
    );

    public static (string Subject, string Html) ScrimTimeProposed(string proposingTeamName, string proposedAt, string sessionUrl) =>
    (
        $"{E(proposingTeamName)} proposed a new time",
        Wrap($"Time proposal from {E(proposingTeamName)}", "Time Proposed", $"""
            {Eyebrow("Find scrims")}
            {H1("New time proposal")}
            {P($"<strong style='color:{Headline};'>{E(proposingTeamName)}</strong> has proposed a new time for your scrim. Review and accept or suggest a different time.")}
            {SpecOpen()}
              {InfoRow("Proposed time", E(proposedAt), isLast: true)}
            {SpecClose}
            {Btn(sessionUrl, "Review Proposal")}
        """)
    );

    public static (string Subject, string Html) ScrimTimeClearRequested(string requestingTeamName, string sessionUrl) =>
    (
        $"{E(requestingTeamName)} asked to clear the scrim time",
        Wrap($"Clear-time request from {E(requestingTeamName)}", "Clear Time Request", $"""
            {Eyebrow("Find scrims")}
            {H1("Clear the agreed time?")}
            {P($"<strong style='color:{Headline};'>{E(requestingTeamName)}</strong> has asked to clear the agreed time for your scrim. You can accept or keep the current schedule.")}
            {Btn(sessionUrl, "Review Request")}
        """)
    );

    public static (string Subject, string Html) ScrimTimeAccepted(string acceptingTeamName, string scheduledAt, string sessionUrl) =>
    (
        $"{E(acceptingTeamName)} confirmed the scrim time",
        Wrap($"Time confirmed by {E(acceptingTeamName)}", "Time Confirmed", $"""
            {Eyebrow("Find scrims")}
            {H1("Time confirmed.")}
            {P($"<strong style='color:{Headline};'>{E(acceptingTeamName)}</strong> accepted the proposed time. Your scrim is now scheduled.")}
            {SpecOpen()}
              {InfoRow("Scheduled", E(scheduledAt), isLast: true)}
            {SpecClose}
            {Btn(sessionUrl, "Open Session")}
        """)
    );

    public static (string Subject, string Html) ScrimTimeReset(string sessionUrl) =>
    (
        "Scrim time proposal cleared",
        Wrap("The proposed time was cleared", "Time Reset", $"""
            {Eyebrow("Find scrims")}
            {H1("Time proposal cleared")}
            {P("The proposed scrim time has been cleared. Use the session workspace to propose a new time that works for both teams.")}
            {Btn(sessionUrl, "Open Session")}
        """)
    );

    public static (string Subject, string Html) ScrimResultSubmitted(string opponentName, string score, string sessionUrl) =>
    (
        $"{E(opponentName)} submitted scrim results",
        Wrap($"Results submitted by {E(opponentName)}", "Results Submitted", $"""
            {Eyebrow("Find scrims")}
            {H1("Results submitted")}
            {P($"<strong style='color:{Headline};'>{E(opponentName)}</strong> has submitted the scrim results. Review and confirm or raise a dispute within the session.")}
            {(string.IsNullOrWhiteSpace(score) ? "" : $"""
            {SpecOpen()}
              {InfoRow("Reported score", E(score), isLast: true)}
            {SpecClose}
            """)}
            {Btn(sessionUrl, "Review Results")}
        """)
    );

    public static (string Subject, string Html) ScrimDisputeRaised(string opponentName, string sessionUrl) =>
    (
        $"{E(opponentName)} raised a scrim dispute",
        Wrap($"Dispute raised by {E(opponentName)}", "Dispute Raised", $"""
            {Eyebrow("Find scrims")}
            {H1("Dispute raised")}
            {P($"<strong style='color:{Headline};'>{E(opponentName)}</strong> has raised a dispute on the scrim results. An Esportra admin will review and resolve it.")}
            {Btn(sessionUrl, "View Dispute")}
        """)
    );

    public static (string Subject, string Html) ScrimDisputeResolved(string outcome, string sessionUrl) =>
    (
        "Your scrim dispute has been resolved",
        Wrap("Scrim dispute resolved", "Dispute Resolved", $"""
            {Eyebrow("Find scrims")}
            {H1("Dispute resolved")}
            {P($"An Esportra admin has resolved the scrim dispute. The final outcome is: <strong style='color:{Headline};'>{E(outcome)}</strong>.")}
            {Btn(sessionUrl, "View Session")}
        """)
    );

    public static (string Subject, string Html) BroadcastCrewInvite(string showTitle, string role, string acceptUrl) =>
    (
        $"Broadcast crew invitation: {showTitle.Replace('\r', ' ').Replace('\n', ' ')}",
        Wrap($"Crew invitation for {E(showTitle)}", "Broadcast Invitation", $"""
            {Eyebrow("Esportra Air crew")}
            {H1($"Join the broadcast for {E(showTitle)}")}
            {P($"You have been invited as <strong>{E(role.Replace('_', ' '))}</strong>. Sign in with the invited email address to accept.")}
            {Btn(acceptUrl, "Review invitation")}
            {P("This invitation expires in 72 hours and can be revoked by the organizer.")}
        """)
    );
    // ── Palette ───────────────────────────────────────────────────────────────
    private const string PageBg = "#050505";
    private const string CardBg = "#0a0a0c";
    private const string Hairline = "#1f2937";
    private const string Headline = "#f9fafb";
    private const string BodyText = "#d1d5db";
    private const string MutedText = "#a1a1aa";
    private const string SubtleText = "#6b7280";
    private const string Rose = "#f43f5e";
    private const string RoseLight = "#fb7185";
    private const string DangerLight = "#fca5a5";

    private const string HeadingFont = "'Poppins','Segoe UI',Arial,sans-serif";
    private const string BodyFont = "'Inter','Segoe UI',Arial,sans-serif";
    private const string MonoFont = "'Consolas','Monaco',monospace";

    // &#847; (U+034F COMBINING GRAPHEME JOINER) is the industry-standard preheader
    // filler (Litmus / Mailchimp / Campaign Monitor). Gmail strips &zwnj; and
    // collapses consecutive &nbsp; inside hidden divs, but &#847; between spaces
    // prevents collapsing and is itself counted toward preview length.
    // 100 triples (&#847;&zwnj;&nbsp;) exceed every known preview window (~140 chars).
    private const string PreviewFiller =
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;" +
        "&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;";

    // Built with concatenation (not raw interpolation) to avoid {{ }} escaping in CSS braces.
    private static readonly string DarkModeStyle =
        "<meta name=\"color-scheme\" content=\"light dark\" />" +
        "<meta name=\"supported-color-schemes\" content=\"light dark\" />" +
        "<style>:root { color-scheme: light dark; } " +
        "@media (prefers-color-scheme: dark) { " +
        ".email-bg { background-color: " + PageBg + " !important; color: " + BodyText + " !important; } " +
        ".card-bg { background-color: " + CardBg + " !important; } }" +
        "</style>";

    // ── URL configuration — set via Init() on startup ────────────────────────
    private static string _frontendUrl = "https://esportra.com";
    private static string _supabaseUrl = "https://api.esportra.com";

    /// <summary>
    /// Call once at startup to inject environment-aware URLs into all email templates.
    /// </summary>
    public static void Init(string frontendUrl, string supabaseUrl)
    {
        _frontendUrl = frontendUrl.TrimEnd('/');
        _supabaseUrl = supabaseUrl.TrimEnd('/');
    }

    /// <summary>HTML-encode user-provided values to prevent injection.</summary>
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    // ── Base wrapper ──────────────────────────────────────────────────────────

    private static string LogoRow() => $"""
                <tr>
                  <td style="padding:16px 28px;">
                    <img src="{_supabaseUrl}/storage/v1/object/public/system.assets.website/eSportra-Logo/eSPORTRA-white-transparent.png" alt="Esportra" width="72" style="display:inline-block;border:0;outline:none;" />
                  </td>
                </tr>
                <tr><td style="height:1px;line-height:1px;font-size:0;background:{Rose};">&nbsp;</td></tr>
        """;

    private static string GameBannerRow(string headerImageUrl) => $"""
                <tr>
                  <td style="padding:0;line-height:0;background:#050505;border-bottom:1px solid {Hairline};">
                    <img src="{E(headerImageUrl)}" alt="" width="600" style="display:block;width:100%;max-width:600px;height:auto;border:0;outline:none;" />
                  </td>
                </tr>
        """;

    private static string FooterRow() => $"""
                <tr>
                  <td style="padding:16px 28px;border-top:1px solid {Hairline};">
                    <p style="margin:0;font-family:{MonoFont};font-size:10px;font-weight:bold;letter-spacing:2px;text-transform:uppercase;color:{SubtleText};">&copy; 2026 Esportra &mdash; All rights reserved</p>
                    <p style="margin:6px 0 0;font-size:12px;color:{SubtleText};">Questions? <a href="mailto:support@esportra.com" style="color:{RoseLight};text-decoration:none;">support@esportra.com</a></p>
                  </td>
                </tr>
        """;

    private static string Wrap(string preheader, string subject, string body, string? headerImageUrl = null) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width,initial-scale=1" />
          {DarkModeStyle}
          <link rel="preconnect" href="https://fonts.googleapis.com" />
          <link href="https://fonts.googleapis.com/css2?family=Poppins:wght@700;800&family=Inter:wght@400;600&display=swap" rel="stylesheet" />
          <title>Esportra</title>
        </head>
        <body class="email-bg" style="margin:0;padding:0;background-color:{PageBg};font-family:{BodyFont};color:{BodyText};">
          <div style="display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;mso-hide:all;">{preheader}{PreviewFiller}</div>
          <table width="100%" cellpadding="0" cellspacing="0" class="email-bg" style="background-color:{PageBg};">
            <tr><td align="center" style="padding:32px 16px;">
              <table width="600" cellpadding="0" cellspacing="0" class="card-bg" style="background-color:{CardBg};border:1px solid {Hairline};overflow:hidden;">
                {(string.IsNullOrWhiteSpace(headerImageUrl)
                    ? LogoRow()
                    : LogoRow() + GameBannerRow(headerImageUrl))}
                <!-- Body -->
                <tr><td style="padding:32px 36px;">{body}</td></tr>
                <!-- Footer -->
                {FooterRow()}
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    // ── Type helpers ──────────────────────────────────────────────────────────

    private static string Mono(float size) =>
        $"font-family:{MonoFont};font-size:{size}px;font-weight:bold;letter-spacing:3px;text-transform:uppercase;";

    /// <summary>Mono rose eyebrow — every template leads with one.</summary>
    private static string Eyebrow(string text) =>
        $"""<p style="margin:0 0 14px;{Mono(10)}color:{RoseLight};">// {E(text)}</p>""";

    private static string H1(string text) =>
        $"""<h1 style="margin:0 0 16px;font-family:{HeadingFont};font-size:24px;line-height:1.25;font-weight:800;letter-spacing:-0.5px;text-transform:uppercase;color:{Headline};">{text}</h1>""";

    private static string SectionLabel(string text) =>
        $"""<p style="margin:0 0 12px;{Mono(10)}color:{SubtleText};">{text}</p>""";

    private static string H2(string text) =>
        $"""<p style="margin:24px 0 12px;font-family:{HeadingFont};font-size:15px;font-weight:700;text-transform:uppercase;letter-spacing:-0.25px;color:{Headline};">{text}</p>""";

    private static string P(string text) =>
        $"""<p style="margin:0 0 12px;font-size:15px;line-height:1.65;color:{BodyText};">{text}</p>""";

    private static string Divider() =>
        $"""<hr style="border:none;border-top:1px solid {Hairline};margin:24px 0;" />""";

    // ── Buttons — JACK IN resting state: white fill, matte-black mono label ──

    private const string BtnTypeStyle =
        $"font-family:{MonoFont};font-size:12px;font-weight:bold;letter-spacing:2px;text-transform:uppercase;";

    private static string Btn(string href, string label) =>
        $"""<a href="{E(href)}" style="display:inline-block;background:#ffffff;color:{CardBg};{BtnTypeStyle}padding:14px 32px;text-decoration:none;margin-top:16px;">{label}</a>""";

    private static string BtnSecondary(string href, string label) =>
        $"""<a href="{E(href)}" style="display:inline-block;background:transparent;color:{BodyText};border:1px solid rgba(255,255,255,0.25);{BtnTypeStyle}padding:13px 30px;text-decoration:none;margin-top:8px;">{label}</a>""";

    // ── Spec-sheet primitives ────────────────────────────────────────────────

    private static string InfoRow(string label, string value, bool isLast = false) =>
        $"""
        <tr>
          <td style="padding:12px 16px;{(isLast ? "" : $" border-bottom:1px solid {Hairline};")}">
            <span style="{Mono(10)}letter-spacing:1.5px;color:{SubtleText};">{E(label)}</span>
          </td>
          <td style="padding:12px 16px;{(isLast ? "" : $" border-bottom:1px solid {Hairline};")} text-align:right;">
            <strong style="color:{Headline};font-size:14px;">{value}</strong>
          </td>
        </tr>
        """;

    private static string SpecOpen() =>
        $"""<table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0 24px;border:1px solid {Hairline};">""";

    private const string SpecClose = "</table>";

    /// <summary>Hairline-bordered mono code panel.</summary>
    private static string CodeBox(string label, string code) =>
        $"""
        <div style="margin:24px 0;padding:20px;border:1px solid rgba(244,63,94,0.35);text-align:center;">
          <div style="{Mono(9)}color:{RoseLight};margin-bottom:10px;">{E(label)}</div>
          <div style="font-family:{MonoFont};font-size:26px;font-weight:bold;letter-spacing:6px;color:#ffffff;">{E(code)}</div>
        </div>
        """;

    /// <summary>Status strip — neutral / success (rose) / danger (red).</summary>
    private static string StatusPanel(string tone, string label, string title, string sub) =>
        tone switch
        {
            "success" => $"""
                <table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;border:1px solid rgba(244,63,94,0.4);background:rgba(244,63,94,0.07);">
                  <tr><td style="padding:16px;text-align:center;">
                    <span style="{Mono(9)}color:{RoseLight};">{E(label)}</span>
                    <p style="margin:6px 0 0;font-family:{HeadingFont};font-size:16px;font-weight:700;text-transform:uppercase;color:{Headline};">{title}</p>
                    <p style="margin:4px 0 0;color:{MutedText};font-size:13px;">{sub}</p>
                  </td></tr>
                </table>
                """,
            "danger" => $"""
                <table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;border:1px solid rgba(239,68,68,0.4);background:rgba(239,68,68,0.05);">
                  <tr><td style="padding:16px;text-align:center;">
                    <span style="{Mono(9)}color:{DangerLight};">{E(label)}</span>
                    <p style="margin:6px 0 0;font-family:{HeadingFont};font-size:16px;font-weight:700;text-transform:uppercase;color:{Headline};">{title}</p>
                    <p style="margin:4px 0 0;color:{MutedText};font-size:13px;">{sub}</p>
                  </td></tr>
                </table>
                """,
            _ => $"""
                <table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;border:1px solid {Hairline};background:#0d0d10;">
                  <tr><td style="padding:16px;text-align:center;">
                    <span style="{Mono(9)}color:{SubtleText};">{E(label)}</span>
                    <p style="margin:6px 0 0;font-family:{HeadingFont};font-size:16px;font-weight:700;text-transform:uppercase;color:{Headline};">{title}</p>
                    <p style="margin:4px 0 0;color:{MutedText};font-size:13px;">{sub}</p>
                  </td></tr>
                </table>
                """
        };

    /// <summary>Indexed feature row — rose numeral instead of emoji.</summary>
    private static string FeatureItem(string index, string title, string desc) =>
        $"""
        <tr>
          <td style="padding:10px 12px 10px 0;vertical-align:top;width:44px;">
            <span style="font-family:{MonoFont};font-size:11px;font-weight:bold;color:{RoseLight};">{index}</span>
          </td>
          <td style="padding:10px 0;">
            <strong style="color:{Headline};font-size:14px;">{title}</strong>
            <br/><span style="color:{MutedText};font-size:13px;">{desc}</span>
          </td>
        </tr>
        """;

    // ── Templates ─────────────────────────────────────────────────────────────

    public static (string Subject, string Html) Welcome(string username) =>
    (
        "Welcome to Esportra",
        Wrap("Your competitive journey starts here.", "Welcome to Esportra", $"""
            {Eyebrow("Account created")}
            {H1($"Welcome, {E(username)}")}
            {P("You've joined the premier esports tournament platform. Start by joining or creating a team, then register for upcoming tournaments.")}
            {Btn($"{_frontendUrl}/tournaments", "Browse Tournaments")}
        """)
    );

    public static (string Subject, string Html) TournamentRegistration(
        string username, string tournamentName, string startDate, string tournamentUrl,
        string endDate = "", string game = "", string teamName = "", string registrationType = "") =>
    (
        $"You're registered for {E(tournamentName)}",
        Wrap($"You're in for {E(tournamentName)}", "Tournament Registration", $"""
            {Eyebrow("Registration confirmed")}
            {H1("You're in.")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, you've successfully registered for <strong style='color:{Headline};'>{E(tournamentName)}</strong>.")}
            {SpecOpen()}
              {InfoRow("Tournament", E(tournamentName))}
              {(string.IsNullOrWhiteSpace(game) ? "" : InfoRow("Game", E(game)))}
              {(string.IsNullOrWhiteSpace(teamName) ? "" : InfoRow("Team", E(teamName)))}
              {(string.IsNullOrWhiteSpace(registrationType) ? "" : InfoRow("Type", E(registrationType).ToUpper()))}
              {InfoRow("Start Date", string.IsNullOrWhiteSpace(startDate) ? "TBA" : E(startDate))}
              {(string.IsNullOrWhiteSpace(endDate) ? "" : InfoRow("End Date", E(endDate), isLast: true))}
            {SpecClose}
            {P("Watch your notifications for check-in windows and match schedules. Check-in closes before the bracket is generated — missing it removes your slot.")}
            {P($"<a href='{(string.IsNullOrWhiteSpace(tournamentUrl) ? _frontendUrl : tournamentUrl)}' style='color:{RoseLight};text-decoration:underline;'>View tournament details</a>, or use the button below.")}
            {Btn(string.IsNullOrWhiteSpace(tournamentUrl) ? _frontendUrl : tournamentUrl, "View Tournament")}
        """)
    );

    public static (string Subject, string Html) TeamInvite(
        string inviteeName, string teamName, string captainName, string acceptUrl) =>
    (
        $"You've been invited to join {E(teamName)}",
        Wrap($"Team invite from {E(captainName)}", "Team Invitation", $"""
            {Eyebrow("Team invite")}
            {H1("You've been drafted")}
            {P($"Hi {E(inviteeName)}, <strong style='color:{Headline};'>{E(captainName)}</strong> has invited you to join <strong style='color:{Headline};'>{E(teamName)}</strong>.")}
            {Btn(acceptUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) StaffInvite(
        string inviteeName, string orgName, string role, string permissions, string acceptUrl) =>
    (
        $"Staff invitation: {E(orgName)}",
        Wrap($"You've been invited to staff {E(orgName)}", "Staff Invitation", $"""
            {Eyebrow("Staff invite")}
            {H1($"{E(orgName)} needs you on staff")}
            {P($"Hi {E(inviteeName)}, you've been invited to join <strong style='color:{Headline};'>{E(orgName)}</strong> as <strong style='color:{Headline};'>{E(role)}</strong>.")}
            {SpecOpen()}
              {InfoRow("Role", E(role), isLast: true)}
            {SpecClose}
            {SectionLabel("Permissions")}
            {P(E(permissions))}
            {Btn(acceptUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) TournamentInvite(
        string captainName, string tournamentName, string code, string tournamentUrl, string expiryDate,
        string gameHeaderUrl = "") =>
    (
        $"You're invited to {E(tournamentName)}",
        Wrap($"Invitation code for {E(tournamentName)}", "Tournament Invitation", $"""
            {Eyebrow("Tournament invite")}
            {H1("Your slot is reserved")}
            {P($"Hi {(string.IsNullOrWhiteSpace(captainName) ? "Captain" : E(captainName))}, you've been invited to join <strong style='color:{Headline};'>{E(tournamentName)}</strong>.")}
            {P("Redeem the code below to claim your guaranteed team slot. It's locked to this email address and works once, for team captains only.")}
            {CodeBox("Invite code", code)}
            {(string.IsNullOrWhiteSpace(expiryDate) ? "" : P($"This invitation expires on <strong style='color:{Headline};'>{E(expiryDate)}</strong>."))}
            {Btn(string.IsNullOrWhiteSpace(tournamentUrl) ? _frontendUrl : tournamentUrl, "Join Tournament")}
        """, string.IsNullOrWhiteSpace(gameHeaderUrl) ? null : gameHeaderUrl)
    );

    public static (string Subject, string Html) PartnerInvite(
      string sponsorName,
      string invitationUrl,
      bool accountExists,
      bool requiresPasswordSetup) =>
    (
        $"Partner Portal access: {E(sponsorName)}",
        Wrap("Accept your partner portal invitation", "Partner Portal Invitation", $"""
            {Eyebrow("Partner access")}
            {H1($"{E(sponsorName)} Partner Portal")}
            {P("You've been invited to manage your sponsor account on Esportra.")}
          {P(!accountExists
            ? "Click below to create your Esportra account, set a password, and accept your sponsor invitation."
            : requiresPasswordSetup
            ? "An Esportra account already exists for this email without a password. Click below to securely add a shared Esportra password and accept your sponsor invitation."
                : "Click below, sign in to your existing Esportra account with this email, and accept your sponsor invitation.")}
          {P("This invitation remains available for 24 hours. After acceptance, reopening it with the same account resumes your onboarding.")}
          {Btn(invitationUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) PartnerWelcome(
        string sponsorName, string portalUrl) =>
    (
        $"Partner Portal access: {E(sponsorName)}",
        Wrap("Your partner account is ready", "Partner Portal Access", $"""
            {Eyebrow("Partner access")}
            {H1($"Partner account linked")}
            {P($"Your account is now connected to the <strong style='color:{Headline};'>{E(sponsorName)}</strong> Partner Portal on Esportra.")}
            {Btn(portalUrl, "Go to Partner Portal")}
        """)
    );

    public static (string Subject, string Html) PasswordReset(string resetUrl) =>
    (
        "Reset your Esportra password",
        Wrap("Password reset link", "Password Reset", $"""
            {Eyebrow("Password reset")}
            {H1("Set a new password")}
            {P("Use the button below to choose a new password. This link expires in 1 hour.")}
            {P("If you didn't request a reset, ignore this email — your current password keeps working.")}
            {Btn(resetUrl, "Reset Password")}
        """)
    );

    public static (string Subject, string Html) LicenseApplicationReceived(
        string username, string licenseType, string dashboardUrl) =>
    (
        "Your license application has been received",
        Wrap("We received your license application", "License Application Received", $"""
            {Eyebrow("License application")}
            {H1("Application received")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, thanks for applying for a <strong style='color:{Headline};'>{E(FormatLicenseType(licenseType))}</strong> license on Esportra.")}

            {StatusPanel("neutral", "Status", "Under Review", "Applications are reviewed within 1–3 business days.")}

            {P("You'll get an email as soon as a decision is made. You can track the status any time from your dashboard.")}
            {Btn(dashboardUrl, "View Application Status")}
        """)
    );

    public static (string Subject, string Html) LicenseApproved(
        string username, string licenseType, string licenseId, string issuedAt, string expiresAt, string dashboardUrl) =>
    (
        $"Your {FormatLicenseType(licenseType)} license has been approved",
        Wrap("Your license has been approved", "License Approved", $"""
            {Eyebrow("License approved")}
            {H1("You're licensed")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, your <strong style='color:{Headline};'>{E(FormatLicenseType(licenseType))}</strong> license has been approved.")}

            <!-- License details card -->
            <table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;border:1px solid rgba(244,63,94,0.4);background:rgba(244,63,94,0.06);">
              <tr><td style="padding:14px 16px;text-align:center;border-bottom:1px solid rgba(244,63,94,0.3);">
                <span style="{Mono(11)}color:{RoseLight};">Licensed {E(FormatLicenseType(licenseType))}</span>
              </td></tr>
              <tr><td style="padding:0;">
                <table width="100%" cellpadding="0" cellspacing="0" style="background:{CardBg};">
                  {(string.IsNullOrWhiteSpace(licenseId) ? "" : InfoRow("License ID", E(licenseId)))}
                  {InfoRow("Type", E(FormatLicenseType(licenseType)))}
                  {InfoRow("Status", "Active")}
                  {InfoRow("Issued", string.IsNullOrWhiteSpace(issuedAt) ? DateTime.UtcNow.ToString("MMM dd, yyyy") : E(issuedAt))}
                  {InfoRow("Expires", string.IsNullOrWhiteSpace(expiresAt) ? DateTime.UtcNow.AddYears(1).ToString("MMM dd, yyyy") : E(expiresAt), isLast: true)}
                </table>
              </td></tr>
            </table>

            {Divider()}

            {SectionLabel("What you can do now")}
            <table width="100%" cellpadding="0" cellspacing="0" style="margin-bottom:20px;">
              {GetFeatureItems(licenseType)}
            </table>

            {Divider()}

            {SectionLabel("Get started")}
            {P(GetQuickStartText(licenseType))}

            <div style="margin-top:20px;">
              {Btn(GetPrimaryCta(licenseType, dashboardUrl).Url, GetPrimaryCta(licenseType, dashboardUrl).Label)}
              <br/>
              {BtnSecondary(dashboardUrl + "/settings", "Complete Your Profile")}
            </div>

            {Divider()}

            <p style="margin:0;font-size:13px;line-height:1.6;color:{SubtleText};">
              Need help getting started? Visit our <a href="{_frontendUrl}/help" style="color:{RoseLight};text-decoration:underline;">Help Center</a> or reach out to <a href="mailto:support@esportra.com" style="color:{RoseLight};text-decoration:underline;">support@esportra.com</a>
            </p>
        """)
    );

    public static (string Subject, string Html) LicenseRejected(
        string username, string licenseType, string dashboardUrl) =>
    (
        $"Your {FormatLicenseType(licenseType)} license application was not approved",
        Wrap("Your license application was reviewed", "License Application Update", $"""
            {Eyebrow("Application update")}
            {H1("Application update")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, we reviewed your <strong style='color:{Headline};'>{E(FormatLicenseType(licenseType))}</strong> application.")}

            {StatusPanel("danger", "Caution", "Not Approved", "Review your details and apply again from your verification page.")}

            {P("You can review your details and submit a new application from your verification status page.")}
            {Btn(dashboardUrl, "View Verification Status")}
        """)
    );

    private static string GetFeatureItems(string licenseType) => licenseType switch
    {
        "organizer" => $"""
            {FeatureItem("01", "Create Tournaments", "Set up single/double elimination, Swiss, or custom bracket formats")}
            {FeatureItem("02", "Manage Brackets", "Run matches, handle disputes, and track results in real-time")}
            {FeatureItem("03", "Build Your Staff", "Invite moderators and admins to help manage your events")}
            {FeatureItem("04", "Analytics Dashboard", "Track participation, engagement, and growth metrics")}
            """,
        "venue_owner" => $"""
            {FeatureItem("01", "List Your Venue", "Showcase your gaming space with photos, amenities, and pricing")}
            {FeatureItem("02", "Manage Bookings", "Accept reservations and manage your venue calendar")}
            {FeatureItem("03", "Station Management", "Set up and monitor gaming stations in real-time")}
            {FeatureItem("04", "Revenue Tracking", "Track bookings, revenue, and occupancy analytics")}
            """,
        "broadcaster" => $"""
            {FeatureItem("01", "Stream Tournaments", "Go live on tournaments with integrated broadcasting tools")}
            {FeatureItem("02", "Multi-Match Views", "Switch between matches and provide real-time commentary")}
            {FeatureItem("03", "VOD Management", "Record and manage video-on-demand content")}
            {FeatureItem("04", "Viewer Analytics", "Track viewership, engagement, and stream performance")}
            """,
        _ => ""
    };

    private static string GetQuickStartText(string licenseType) => licenseType switch
    {
        "organizer" => "Create your first tournament and start building your competitive community. You have access to all bracket formats, scheduling tools, and match management features.",
        "venue_owner" => "List your venue and start accepting bookings. Add photos, set your pricing, configure your gaming stations, and go live.",
        "broadcaster" => "Connect your streaming setup and start broadcasting tournaments. You have access to multi-match views and real-time commentary tools.",
        _ => "Head to your dashboard to explore all the features now available to you."
    };

    private static (string Url, string Label) GetPrimaryCta(string licenseType, string dashboardUrl) => licenseType switch
    {
        "organizer" => ($"{dashboardUrl.Replace("/verification-status", "")}/organizer/tournaments/new", "Create Your First Tournament"),
        "venue_owner" => ($"{dashboardUrl.Replace("/verification-status", "")}/venues/new", "List Your Venue"),
        "broadcaster" => (dashboardUrl, "Go to Dashboard"),
        _ => (dashboardUrl, "Go to Dashboard")
    };

    private static string FormatLicenseType(string licenseType) =>
        licenseType switch
        {
            "organizer" => "Organizer",
            "venue_owner" => "Venue Owner",
            "broadcaster" => "Broadcaster",
            _ => licenseType
        };

    // ── Broadcast Template — plain drafted-email format ─────────────────────
    // No card chrome: reads like a human-typed announcement. Small logo line,
    // bold sentence-case title, optional meta micro-line, body paragraphs,
    // team sign-off, single support line.

    public static (string Subject, string Html) Broadcast(
        string title, string content, string broadcastType = "announcement", string priority = "normal",
        string? heroImageUrl = null, string? ctaUrl = null, string? ctaLabel = null)
    {
        var subject = broadcastType switch
        {
            "maintenance" => $"Scheduled maintenance: {title}",
            "system" => $"System notice: {title}",
            "urgent" when priority is "urgent" or "high" => $"Urgent: {title}",
            _ => title
        };

        if (!string.IsNullOrWhiteSpace(heroImageUrl))
            return BroadcastRich(subject, title, content, heroImageUrl, ctaUrl, ctaLabel);

        return BroadcastPlain(subject, title, content, broadcastType, priority, ctaUrl, ctaLabel);
    }

    private static (string Subject, string Html) BroadcastRich(
        string subject, string title, string content,
        string heroImageUrl, string? ctaUrl, string? ctaLabel)
    {
        var paragraphs = content
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var bodyHtml = new StringBuilder();
        foreach (var para in paragraphs)
            bodyHtml.Append(P(E(para)));

        var ctaHtml = !string.IsNullOrWhiteSpace(ctaUrl)
            ? Btn(ctaUrl, string.IsNullOrWhiteSpace(ctaLabel) ? "Learn More" : ctaLabel)
            : "";

        var preheader = string.IsNullOrWhiteSpace(content)
            ? "New message from Esportra"
            : E(content.Length > 120 ? content[..120] + "…" : content);

        var html = Wrap(preheader, subject, $"""
            {Eyebrow("Esportra update")}
            {H1(E(title))}
            {bodyHtml}
            {ctaHtml}
        """, heroImageUrl);

        return (subject, html);
    }

    private static (string Subject, string Html) BroadcastPlain(
        string subject, string title, string content,
        string broadcastType, string priority,
        string? ctaUrl, string? ctaLabel)
    {
        var meta = new List<string>();
        var typeLabel = GetBroadcastTag(broadcastType).Label;
        if (!string.Equals(typeLabel, "Announcement", StringComparison.OrdinalIgnoreCase))
            meta.Add(typeLabel);
        if (priority == "urgent") meta.Add("Urgent");
        else if (priority == "high") meta.Add("High priority");

        var paragraphs = content
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var bodyHtml = new StringBuilder();
        foreach (var para in paragraphs)
            bodyHtml.Append($"""<p style="margin:0 0 14px;font-size:15px;line-height:1.7;color:{BodyText};">{E(para)}</p>""");

        var metaRow = meta.Count > 0
            ? $"""
                <tr><td style="padding:0 0 18px;">
                  <span style="{Mono(10)}color:{SubtleText};">{E(string.Join(" &middot; ", meta))}</span>
                </td></tr>
                """
            : "";

        var ctaRow = !string.IsNullOrWhiteSpace(ctaUrl)
            ? $"""
                <tr><td style="padding-top:16px;">
                  {Btn(ctaUrl, string.IsNullOrWhiteSpace(ctaLabel) ? "Learn More" : ctaLabel)}
                </td></tr>
                """
            : "";

        var preheaderText = string.IsNullOrWhiteSpace(content)
            ? "New message from Esportra"
            : E(content.Length > 120 ? content[..120] + "…" : content);

        var html = $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width,initial-scale=1" />
          {DarkModeStyle}
          <title>Esportra</title>
        </head>
        <body class="email-bg" style="margin:0;padding:0;background-color:{PageBg};font-family:{BodyFont};color:{BodyText};">
          <div style="display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;mso-hide:all;">{preheaderText}{PreviewFiller}</div>
          <table width="100%" cellpadding="0" cellspacing="0" class="email-bg" style="background-color:{PageBg};">
            <tr><td align="center" style="padding:40px 20px;">
              <table width="560" cellpadding="0" cellspacing="0">
                <tr><td style="padding-bottom:28px;">
                  <img src="{_supabaseUrl}/storage/v1/object/public/system.assets.website/eSportra-Logo/eSPORTRA-white-transparent.png" alt="Esportra" width="64" style="display:inline-block;border:0;outline:none;" />
                </td></tr>
                <tr><td style="padding-bottom:6px;">
                  <span style="font-size:17px;font-weight:600;color:{Headline};">{E(title)}</span>
                </td></tr>
                {metaRow}
                <tr><td>{bodyHtml}</td></tr>
                {ctaRow}
                <tr><td style="padding-top:20px;">
                  <p style="margin:0;font-size:14px;color:{MutedText};">&mdash; The Esportra Team</p>
                  <p style="margin:16px 0 0;font-size:12px;color:{SubtleText};">Questions? <a href="mailto:support@esportra.com" style="color:{RoseLight};text-decoration:none;">support@esportra.com</a></p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

        return (subject, html);
    }

    private static (string Label, string Color) GetBroadcastTag(string broadcastType) =>
        broadcastType switch
        {
            "maintenance" => ("Maintenance", SubtleText),
            "promotion" => ("Special Offer", RoseLight),
            "tournament" => ("Tournament Update", RoseLight),
            "system" => ("System Notice", SubtleText),
            _ => ("Announcement", RoseLight)
        };

    public static (string Subject, string Html) MatchChatMessage(
        string senderTeamName, string messagePreview, string matchRoomUrl, int unreadCount = 1)
    {
        var subject = $"You have {unreadCount} unread message{(unreadCount == 1 ? "" : "s")} from {E(senderTeamName)} in your match room";
        var body = $"""
            {Eyebrow("match comms")}
            {H1($"You have {unreadCount} unread message{(unreadCount == 1 ? "" : "s")}")}
            {P($"<strong style=\"color:{Headline};\">{E(senderTeamName)}</strong> sent a message in your match room.")}
            <div style="margin:20px 0;padding:16px 20px;border-left:3px solid {Rose};background:rgba(244,63,94,0.06);">
              <p style="margin:0;font-size:14px;line-height:1.6;color:{BodyText};font-style:italic;">&ldquo;{E(messagePreview)}&rdquo;</p>
            </div>
            {Btn(matchRoomUrl, "Open match room")}
            {Divider()}
            {P($"<span style=\"color:{SubtleText};font-size:13px;\">You will not receive another notification for this match for the next 5 minutes.</span>")}
            """;

        var html = Wrap("New message from your opponent in the match room.", subject, body);
        return (subject, html);
    }

    public static (string Subject, string Html) DisputeResolved(
        string referenceNumber, string title, string status, string resolutionNotes,
        string tournamentName, string disputeUrl, string recipientType, string filerName = "")
    {
        var statusText = status == "resolved" ? "resolved" : "rejected";
        var subject = recipientType == "filer"
            ? $"Your dispute #{referenceNumber} has been {statusText}"
            : $"Dispute #{referenceNumber} {statusText} in {tournamentName}";

        var body = $"""
            {Eyebrow("dispute update")}
            {H1($"Dispute {statusText}")}
            {P($"Dispute <strong style=\"color:{Headline};\">{E(referenceNumber)}</strong> has been {statusText} by the tournament organizer.")}
            {SpecOpen()}
            {InfoRow("Dispute", E(title))}
            {InfoRow("Status", statusText == "resolved" ? "✅ Resolved" : "❌ Rejected")}
            {InfoRow("Tournament", E(tournamentName))}
            {(recipientType == "organizer" && !string.IsNullOrEmpty(filerName) ? InfoRow("Filed by", E(filerName)) : "")}
            {InfoRow("Resolution Notes", E(resolutionNotes), isLast: true)}
            {SpecClose}
            {Btn(disputeUrl, "View dispute details")}
            """;

        var preheader = $"Your dispute #{referenceNumber} has been {statusText}.";
        var html = Wrap(preheader, subject, body);
        return (subject, html);
    }

    public static (string Subject, string Html) DisputeComment(
        string referenceNumber, string commenterName, string commentPreview,
        string disputeUrl, string tournamentName)
    {
        var subject = $"New comment on your dispute #{referenceNumber}";
        var body = $"""
            {Eyebrow("dispute activity")}
            {H1("New comment on your dispute")}
            {P($"<strong style=\"color:{Headline};\">{E(commenterName)}</strong> added a comment to dispute <strong style=\"color:{Headline};\">{E(referenceNumber)}</strong> in {E(tournamentName)}.")}
            <div style="margin:20px 0;padding:16px 20px;border-left:3px solid {Rose};background:rgba(244,63,94,0.06);">
              <p style="margin:0;font-size:14px;line-height:1.6;color:{BodyText};font-style:italic;">&ldquo;{E(commentPreview)}&rdquo;</p>
            </div>
            {Btn(disputeUrl, "View full discussion")}
            {Divider()}
            {P($"<span style=\"color:{SubtleText};font-size:13px;\">You will not receive another notification for this dispute for the next 5 minutes.</span>")}
            """;

        var preheader = $"New comment from {E(commenterName)} on your dispute.";
        var html = Wrap(preheader, subject, body);
        return (subject, html);
    }

    internal static (string Subject, string Html) BuildTemplate(EmailType type, object data)
    {
        var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            System.Text.Json.JsonSerializer.Serialize(data)) ?? [];

        string Get(string key, string fallback = "") =>
            d.TryGetValue(key, out var v)
                ? v.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => v.GetString() ?? fallback,
                    System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    _ => fallback
                }
                : fallback;

        return type switch
        {
            EmailType.BroadcastCrewInvite => BroadcastCrewInvite(Get("showTitle"), Get("role"), Get("acceptUrl")),
            EmailType.Welcome => Welcome(Get("username")),
            EmailType.TournamentRegistration =>
                TournamentRegistration(
                    Get("username"), Get("tournamentName"),
                    Get("startDate"), Get("tournamentUrl"),
                    Get("endDate"), Get("game"),
                    Get("teamName"), Get("registrationType")),
            EmailType.TeamInvite =>
                TeamInvite(Get("inviteeName"), Get("teamName"), Get("captainName"), Get("acceptUrl")),
            EmailType.StaffInvite =>
                StaffInvite(Get("inviteeName"), Get("orgName"), Get("role"), Get("permissions"), Get("acceptUrl")),
            EmailType.PartnerInvite =>
                PartnerInvite(
                    Get("sponsorName"), Get("invitationUrl"),
                    bool.TryParse(Get("accountExists"), out var accountExists) && accountExists,
                    bool.TryParse(Get("requiresPasswordSetup"), out var requiresPasswordSetup) && requiresPasswordSetup),
            EmailType.PartnerWelcome => PartnerWelcome(Get("sponsorName"), Get("portalUrl")),
            EmailType.PasswordReset => PasswordReset(Get("resetUrl")),
            EmailType.LicenseApplicationReceived =>
                LicenseApplicationReceived(Get("username"), Get("licenseType"), Get("dashboardUrl")),
            EmailType.LicenseApproved =>
                LicenseApproved(
                    Get("username"), Get("licenseType"), Get("licenseId"),
                    Get("issuedAt"), Get("expiresAt"), Get("dashboardUrl")),
            EmailType.LicenseRejected =>
                LicenseRejected(Get("username"), Get("licenseType"), Get("dashboardUrl")),
            EmailType.TournamentInvite =>
                TournamentInvite(
                    Get("captainName"), Get("tournamentName"),
                    Get("code"), Get("tournamentUrl"), Get("expiryDate"), Get("gameHeaderUrl")),
            EmailType.Broadcast =>
                Broadcast(
                    Get("title"), Get("content"),
                    Get("broadcastType", "announcement"), Get("priority", "normal"),
                    heroImageUrl: Get("heroImageUrl") is { Length: > 0 } h ? h : null,
                    ctaUrl: Get("ctaUrl") is { Length: > 0 } cu ? cu : null,
                    ctaLabel: Get("ctaLabel") is { Length: > 0 } cl ? cl : null),
            EmailType.MatchChatMessage =>
                MatchChatMessage(
                    Get("senderTeamName"), Get("messagePreview"), Get("matchRoomUrl"),
                    int.TryParse(Get("unreadCount"), out var uc) ? uc : 1),
            EmailType.DisputeResolved =>
                DisputeResolved(
                    Get("referenceNumber"), Get("title"), Get("status"),
                    Get("resolutionNotes"), Get("tournamentName"),
                    Get("disputeUrl"), Get("recipientType"), Get("filerName")),
            EmailType.DisputeComment =>
                DisputeComment(
                    Get("referenceNumber"), Get("commenterName"),
                    Get("commentPreview"), Get("disputeUrl"), Get("tournamentName")),
            // LFG
            EmailType.LfgInterestReceived =>
                LfgInterestReceived(Get("fromUsername"), Get("listingUrl")),
            EmailType.LfgInterestAccepted =>
                LfgInterestAccepted(Get("ownerUsername"), Get("conversationUrl")),
            EmailType.LfgInterestDeclined =>
                LfgInterestDeclined(Get("discoveryUrl")),
            EmailType.LfgListingExpiring =>
                LfgListingExpiring(Get("username"), Get("listingUrl"), Get("expiresAt")),
            EmailType.LfgListingExpired =>
                LfgListingExpired(Get("username"), Get("discoveryUrl")),
            EmailType.LfgSwipeMatch =>
                LfgSwipeMatch(Get("matchedUsername"), Get("matchUrl")),
            // Scrims
            EmailType.ScrimOfferReceived =>
                ScrimOfferReceived(Get("offeringTeamName"), Get("proposedAt"), Get("workspaceUrl")),
            EmailType.ScrimOfferAccepted =>
                ScrimOfferAccepted(Get("listingTeamName"), Get("sessionUrl"),
                    Get("scheduledAt") is { Length: > 0 } sa ? sa : null),
            EmailType.ScrimOfferDeclined =>
                ScrimOfferDeclined(Get("discoveryUrl")),
            EmailType.ScrimOfferWithdrawn =>
                ScrimOfferWithdrawn(Get("offeringTeamName"), Get("workspaceUrl")),
            EmailType.ScrimOfferExpired =>
                ScrimOfferExpired(Get("listingTeamName"), Get("discoveryUrl")),
            EmailType.ScrimSessionCancelled =>
                ScrimSessionCancelled(Get("cancellerName"), Get("workspaceUrl")),
            EmailType.ScrimReminder =>
                ScrimReminder(Get("opponentName"), Get("scheduledAt"), Get("sessionUrl")),
            EmailType.ScrimCheckinOpen =>
                ScrimCheckinOpen(Get("opponentName"), Get("scheduledAt"), Get("sessionUrl")),
            EmailType.ScrimTimeoutCancelled =>
                ScrimTimeoutCancelled(Get("sessionUrl")),
            EmailType.ScrimTimeProposed =>
                ScrimTimeProposed(Get("proposingTeamName"), Get("proposedAt"), Get("sessionUrl")),
            EmailType.ScrimTimeClearRequested =>
                ScrimTimeClearRequested(Get("requestingTeamName"), Get("sessionUrl")),
            EmailType.ScrimTimeAccepted =>
                ScrimTimeAccepted(Get("acceptingTeamName"), Get("scheduledAt"), Get("sessionUrl")),
            EmailType.ScrimTimeReset =>
                ScrimTimeReset(Get("sessionUrl")),
            EmailType.ScrimResultSubmitted =>
                ScrimResultSubmitted(Get("opponentName"), Get("score"), Get("sessionUrl")),
            EmailType.ScrimDisputeRaised =>
                ScrimDisputeRaised(Get("opponentName"), Get("sessionUrl")),
            EmailType.ScrimDisputeResolved =>
                ScrimDisputeResolved(Get("outcome"), Get("sessionUrl")),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown email type")
        };
    }
}
