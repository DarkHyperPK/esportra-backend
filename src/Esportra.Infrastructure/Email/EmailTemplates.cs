using System.Net;
using System.Text;

namespace Esportra.Infrastructure.Email;

/// <summary>
/// Produces branded HTML email bodies. Ported from supabase/functions/send-email/templates.ts.
/// Dark theme: #0a0a0a base, #e11d48 rose CTA.
/// </summary>
public static class EmailTemplates
{
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

    private static string DefaultHeaderRow() => $"""
                <tr>
                  <td style="background:linear-gradient(135deg,#e11d48,#be123c);padding:24px 32px;text-align:center;">
                    <img src="{_supabaseUrl}/storage/v1/object/public/system.assets.website/eSportra-Logo/eSPORTRA-white-transparent.png" alt="Esportra" width="120" style="display:inline-block;border:0;outline:none;" />
                  </td>
                </tr>
        """;

    private static string GameHeaderRow(string headerImageUrl) => $"""
                <tr>
                  <td style="padding:0;line-height:0;background:#050505;">
                    <img src="{E(headerImageUrl)}" alt="" width="600" style="display:block;width:100%;max-width:600px;height:auto;border:0;outline:none;" />
                  </td>
                </tr>
        """;

    private static string Wrap(string preheader, string subject, string body, string? headerImageUrl = null) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width,initial-scale=1" />
          <title>{subject}</title>
        </head>
        <body style="margin:0;padding:0;background:#0a0a0a;font-family:Inter,Segoe UI,sans-serif;color:#e5e7eb;">
          <div style="display:none;max-height:0;overflow:hidden;">{preheader}</div>
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#0a0a0a;">
            <tr><td align="center" style="padding:32px 16px;">
              <table width="600" cellpadding="0" cellspacing="0" style="background:#111111;border-radius:12px;border:1px solid #1f2937;overflow:hidden;">
                <!-- Header -->
                {(string.IsNullOrWhiteSpace(headerImageUrl) ? DefaultHeaderRow() : GameHeaderRow(headerImageUrl))}
                <!-- Body -->
                <tr><td style="padding:32px;">{body}</td></tr>
                <!-- Footer -->
                <tr>
                  <td style="padding:16px 32px;border-top:1px solid #1f2937;text-align:center;">
                    <p style="margin:0;font-size:12px;color:#6b7280;">© 2026 Esportra. All rights reserved.</p>
                  </td>
                </tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    private static string Btn(string href, string label) =>
        $"""<a href="{E(href)}" style="display:inline-block;background:linear-gradient(135deg,#e11d48,#be123c);color:#fff;font-weight:600;font-size:14px;padding:12px 28px;border-radius:8px;text-decoration:none;margin-top:16px;">{label}</a>""";

    private static string BtnSecondary(string href, string label) =>
        $"""<a href="{E(href)}" style="display:inline-block;background:transparent;color:#e11d48;font-weight:600;font-size:14px;padding:10px 24px;border-radius:8px;text-decoration:none;margin-top:8px;border:1px solid #e11d48;">{label}</a>""";

    private static string H1(string text) =>
        $"""<h1 style="margin:0 0 16px;font-size:22px;font-weight:700;color:#f9fafb;">{text}</h1>""";

    private static string P(string text) =>
        $"""<p style="margin:0 0 12px;font-size:15px;line-height:1.6;color:#d1d5db;">{text}</p>""";

    private static string Divider() =>
        """<hr style="border:none;border-top:1px solid #1f2937;margin:24px 0;" />""";

    private static string FeatureItem(string emoji, string title, string desc) =>
        $"""
        <tr>
          <td style="padding:8px 12px;vertical-align:top;width:36px;">
            <span style="font-size:20px;">{emoji}</span>
          </td>
          <td style="padding:8px 12px;">
            <strong style="color:#f9fafb;font-size:14px;">{title}</strong>
            <br/><span style="color:#9ca3af;font-size:13px;">{desc}</span>
          </td>
        </tr>
        """;

    private static string InfoRow(string label, string value, bool isLast = false) =>
        $"""
        <tr>
          <td style="padding:12px 16px;{(isLast ? "" : " border-bottom:1px solid #1f2937;")}">
            <span style="font-size:12px;color:#9ca3af;text-transform:uppercase;letter-spacing:1px;">{label}</span>
          </td>
          <td style="padding:12px 16px;{(isLast ? "" : " border-bottom:1px solid #1f2937;")} text-align:right;">
            <strong style="color:#f9fafb;font-size:14px;">{value}</strong>
          </td>
        </tr>
        """;

    // ── Templates ─────────────────────────────────────────────────────────────

    public static (string Subject, string Html) Welcome(string username) =>
    (
        "Welcome to Esportra!",
        Wrap("Your competitive journey starts here.", "Welcome to Esportra", $"""
            {H1($"Welcome, {E(username)}!")}
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
            {H1("Registration Confirmed! 🎮")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, you've successfully registered for <strong style='color:#f9fafb;'>{E(tournamentName)}</strong>.")}
            <table width="100%" cellpadding="0" cellspacing="0" style="margin:16px 0 24px; border:1px solid #1f2937; border-radius:8px; overflow:hidden;">
              <tr style="background:#1a1a2e;">
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937;">
                  <span style="font-size:12px; color:#9ca3af; text-transform:uppercase; letter-spacing:1px;">Tournament</span>
                </td>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937; text-align:right;">
                  <strong style="color:#f9fafb; font-size:14px;">{E(tournamentName)}</strong>
                </td>
              </tr>
              {(string.IsNullOrWhiteSpace(game) ? "" : $"""
              <tr>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937;">
                  <span style="font-size:12px; color:#9ca3af; text-transform:uppercase; letter-spacing:1px;">Game</span>
                </td>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937; text-align:right;">
                  <strong style="color:#f9fafb; font-size:14px;">{E(game)}</strong>
                </td>
              </tr>
              """)}
              {(string.IsNullOrWhiteSpace(teamName) ? "" : $"""
              <tr>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937;">
                  <span style="font-size:12px; color:#9ca3af; text-transform:uppercase; letter-spacing:1px;">Team</span>
                </td>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937; text-align:right;">
                  <strong style="color:#f9fafb; font-size:14px;">{E(teamName)}</strong>
                </td>
              </tr>
              """)}
              {(string.IsNullOrWhiteSpace(registrationType) ? "" : $"""
              <tr>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937;">
                  <span style="font-size:12px; color:#9ca3af; text-transform:uppercase; letter-spacing:1px;">Type</span>
                </td>
                <td style="padding:12px 16px; border-bottom:1px solid #1f2937; text-align:right;">
                  <strong style="color:#f9fafb; font-size:14px;">{E(registrationType).ToUpper()}</strong>
                </td>
              </tr>
              """)}
              <tr>
                <td style="padding:12px 16px;{(string.IsNullOrWhiteSpace(endDate) ? "" : " border-bottom:1px solid #1f2937;")}">
                  <span style="font-size:12px; color:#9ca3af; text-transform:uppercase; letter-spacing:1px;">Start Date</span>
                </td>
                <td style="padding:12px 16px;{(string.IsNullOrWhiteSpace(endDate) ? "" : " border-bottom:1px solid #1f2937;")} text-align:right;">
                  <strong style="color:#f9fafb; font-size:14px;">{(string.IsNullOrWhiteSpace(startDate) ? "TBA" : E(startDate))}</strong>
                </td>
              </tr>
              {(string.IsNullOrWhiteSpace(endDate) ? "" : $"""
              <tr>
                <td style="padding:12px 16px;">
                  <span style="font-size:12px; color:#9ca3af; text-transform:uppercase; letter-spacing:1px;">End Date</span>
                </td>
                <td style="padding:12px 16px; text-align:right;">
                  <strong style="color:#f9fafb; font-size:14px;">{E(endDate)}</strong>
                </td>
              </tr>
              """)}
            </table>
            {P("Keep an eye on your notifications for check-in reminders and match schedules.")}
            {P($"<a href='{(string.IsNullOrWhiteSpace(tournamentUrl) ? _frontendUrl : Uri.EscapeDataString(tournamentUrl))}' style='color:#e11d48; text-decoration:underline;'>Visit Esportra</a> to see more details about the tournament.")}
            {Btn(string.IsNullOrWhiteSpace(tournamentUrl) ? _frontendUrl : Uri.EscapeDataString(tournamentUrl), "View Tournament")}
        """)
    );

    public static (string Subject, string Html) TeamInvite(
        string inviteeName, string teamName, string captainName, string acceptUrl) =>
    (
        $"You've been invited to join {E(teamName)}",
        Wrap($"Team invite from {E(captainName)}", "Team Invitation", $"""
            {H1("Team Invitation")}
            {P($"Hi {E(inviteeName)}, <strong style='color:#f9fafb;'>{E(captainName)}</strong> has invited you to join <strong style='color:#f9fafb;'>{E(teamName)}</strong>.")}
            {Btn(acceptUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) StaffInvite(
        string inviteeName, string orgName, string role, string permissions, string acceptUrl) =>
    (
        $"Staff invitation: {E(orgName)}",
        Wrap($"You've been invited to staff {E(orgName)}", "Staff Invitation", $"""
            {H1("Staff Invitation")}
            {P($"Hi {E(inviteeName)}, you've been invited to join <strong style='color:#f9fafb;'>{E(orgName)}</strong> as <strong style='color:#f9fafb;'>{E(role)}</strong>.")}
            {P($"<strong style='color:#f9fafb;'>Permissions:</strong> {E(permissions)}")}
            {Btn(acceptUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) TournamentInvite(
        string captainName, string tournamentName, string code, string tournamentUrl, string expiryDate,
        string gameHeaderUrl = "") =>
    (
        $"You're invited to {E(tournamentName)}",
        Wrap($"Invitation code for {E(tournamentName)}", "Tournament Invitation", $"""
            {H1("Tournament Invitation")}
            {P($"Hi {(string.IsNullOrWhiteSpace(captainName) ? "Captain" : E(captainName))}, you've been invited to join <strong style='color:#f9fafb;'>{E(tournamentName)}</strong>.")}
            {P("Use the invitation code below to redeem your guaranteed team slot. The code is locked to this email address and can only be redeemed once by a team captain.")}
            <div style="margin:24px 0;padding:18px;border:1px solid #7c3aed;border-radius:10px;background:#181028;text-align:center;">
              <div style="font-size:12px;color:#c4b5fd;text-transform:uppercase;letter-spacing:1.5px;margin-bottom:8px;">Invite Code</div>
              <div style="font-family:Consolas,Monaco,monospace;font-size:28px;font-weight:800;letter-spacing:4px;color:#fff;">{E(code)}</div>
            </div>
            {(string.IsNullOrWhiteSpace(expiryDate) ? "" : P($"This invitation expires on <strong style='color:#f9fafb;'>{E(expiryDate)}</strong>."))}
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
            {H1($"Welcome to the {E(sponsorName)} Partner Portal")}
            {P("You've been invited to manage your sponsor account on Esportra.")}
          {P(!accountExists
            ? "Click below to create your Esportra account, set a password, and accept your sponsor invitation."
            : requiresPasswordSetup
                ? "An Esportra account already exists for this email. Click below and continue using the sign-in method linked to that account."
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
            {H1($"Welcome back, {E(sponsorName)} partner!")}
            {P("Your account has been linked to the Esportra Partner Portal.")}
            {Btn(portalUrl, "Go to Partner Portal")}
        """)
    );

    public static (string Subject, string Html) PasswordReset(string resetUrl) =>
    (
        "Reset your Esportra password",
        Wrap("Password reset link", "Password Reset", $"""
            {H1("Reset Your Password")}
            {P("Click the button below to set a new password. This link expires in 1 hour.")}
            {P("If you didn't request a password reset, you can safely ignore this email.")}
            {Btn(resetUrl, "Reset Password")}
        """)
    );

    public static (string Subject, string Html) LicenseApplicationReceived(
        string username, string licenseType, string dashboardUrl) =>
    (
        "Your license application has been received",
        Wrap("We received your license application", "License Application Received", $"""
            {H1("Application Received! 📋")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, thank you for applying for a <strong style='color:#f9fafb;'>{E(FormatLicenseType(licenseType))}</strong> license on Esportra.")}

            <table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;border:1px solid #1f2937;border-radius:8px;overflow:hidden;">
              <tr style="background:#1a1a2e;">
                <td style="padding:16px;text-align:center;">
                  <span style="font-size:32px;">⏳</span>
                  <p style="margin:8px 0 0;color:#fbbf24;font-weight:600;font-size:14px;">Under Review</p>
                  <p style="margin:4px 0 0;color:#9ca3af;font-size:13px;">Our team reviews all applications within 1–3 business days.</p>
                </td>
              </tr>
            </table>

            {P("You'll receive an email as soon as a decision has been made. In the meantime, you can track your application status from your dashboard.")}
            {Btn(dashboardUrl, "View Application Status")}
        """)
    );

    public static (string Subject, string Html) LicenseApproved(
        string username, string licenseType, string licenseId, string issuedAt, string expiresAt, string dashboardUrl) =>
    (
        $"Your {FormatLicenseType(licenseType)} license has been approved!",
        Wrap("Your license has been approved", "License Approved", $"""
            {H1("License Approved! 🎉")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, great news — your <strong style='color:#f9fafb;'>{E(FormatLicenseType(licenseType))}</strong> license has been approved.")}

            <!-- License Details Card -->
            <table width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;border:1px solid #1f2937;border-radius:8px;overflow:hidden;">
              <tr><td style="background:linear-gradient(135deg,#065f46,#047857);padding:16px;text-align:center;">
                <p style="margin:0;color:#fff;font-weight:700;font-size:16px;">Licensed {FormatLicenseType(licenseType)}</p>
              </td></tr>
              <tr><td style="padding:0;">
                <table width="100%" cellpadding="0" cellspacing="0" style="background:#0d1117;">
                  {(string.IsNullOrWhiteSpace(licenseId) ? "" : InfoRow("License ID", licenseId))}
                  {InfoRow("Type", FormatLicenseType(licenseType))}
                  {InfoRow("Status", "Active")}
                  {InfoRow("Issued", string.IsNullOrWhiteSpace(issuedAt) ? DateTime.UtcNow.ToString("MMM dd, yyyy") : issuedAt)}
                  {InfoRow("Expires", string.IsNullOrWhiteSpace(expiresAt) ? DateTime.UtcNow.AddYears(1).ToString("MMM dd, yyyy") : expiresAt, isLast: true)}
                </table>
              </td></tr>
            </table>

            {Divider()}

            <!-- What You Can Do Now -->
            <h2 style="margin:0 0 16px;font-size:18px;font-weight:600;color:#f9fafb;">What you can do now</h2>
            <table width="100%" cellpadding="0" cellspacing="0" style="margin-bottom:20px;">
              {GetFeatureItems(licenseType)}
            </table>

            {Divider()}

            <!-- Quick Start -->
            <h2 style="margin:0 0 12px;font-size:18px;font-weight:600;color:#f9fafb;">Get started</h2>
            {P(GetQuickStartText(licenseType))}

            <div style="text-align:center;margin-top:20px;">
              {Btn(GetPrimaryCta(licenseType, dashboardUrl).Url, GetPrimaryCta(licenseType, dashboardUrl).Label)}
              <br/>
              {BtnSecondary(dashboardUrl + "/settings", "Complete Your Profile")}
            </div>

            {Divider()}

            <p style="margin:0;font-size:13px;line-height:1.6;color:#6b7280;text-align:center;">
              Need help getting started? Visit our <a href="{_frontendUrl}/help" style="color:#e11d48;text-decoration:underline;">Help Center</a> or reach out to <a href="mailto:support@esportra.com" style="color:#e11d48;text-decoration:underline;">support@esportra.com</a>
            </p>
        """)
    );

    public static (string Subject, string Html) LicenseRejected(
        string username, string licenseType, string dashboardUrl) =>
    (
        $"Your {FormatLicenseType(licenseType)} license application was not approved",
        Wrap("Your license application was reviewed", "License Application Update", $"""
            {H1("Application Update")}
            {P($"Hi {(string.IsNullOrWhiteSpace(username) ? "there" : E(username))}, we reviewed your <strong style='color:#f9fafb;'>{E(FormatLicenseType(licenseType))}</strong> application.")} 
            {P("At this time, your application was not approved. You can review your details and submit a new application from your verification status page.")}
            {Btn(dashboardUrl, "View Verification Status")}
        """)
    );

    private static string GetFeatureItems(string licenseType) => licenseType switch
    {
        "organizer" => $"""
            {FeatureItem("🏆", "Create Tournaments", "Set up single/double elimination, Swiss, or custom bracket formats")}
            {FeatureItem("📊", "Manage Brackets", "Run matches, handle disputes, and track results in real-time")}
            {FeatureItem("👥", "Build Your Staff", "Invite moderators and admins to help manage your events")}
            {FeatureItem("📈", "Analytics Dashboard", "Track participation, engagement, and growth metrics")}
            """,
        "venue_owner" => $"""
            {FeatureItem("🏟️", "List Your Venue", "Showcase your gaming space with photos, amenities, and pricing")}
            {FeatureItem("📅", "Manage Bookings", "Accept reservations and manage your venue calendar")}
            {FeatureItem("🖥️", "Station Management", "Set up and monitor gaming stations in real-time")}
            {FeatureItem("💰", "Revenue Tracking", "Track bookings, revenue, and occupancy analytics")}
            """,
        "broadcaster" => $"""
            {FeatureItem("🎙️", "Stream Tournaments", "Go live on tournaments with integrated broadcasting tools")}
            {FeatureItem("📺", "Multi-Match Views", "Switch between matches and provide real-time commentary")}
            {FeatureItem("🎬", "VOD Management", "Record and manage video-on-demand content")}
            {FeatureItem("📊", "Viewer Analytics", "Track viewership, engagement, and stream performance")}
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

    // ── Broadcast Template ───────────────────────────────────────────────────

    public static (string Subject, string Html) Broadcast(
        string title, string content, string broadcastType = "announcement", string priority = "normal")
    {
        var (headerHtml, accentColor, icon) = GetBroadcastTypeStyle(broadcastType);
        var priorityBadge = GetPriorityBadge(priority);
        var subject = broadcastType switch
        {
            "maintenance" => $"⚠️ {E(title)}",
            "system" => $"🛡️ {E(title)}",
            "urgent" when priority is "urgent" or "high" => $"🚨 {E(title)}",
            _ => E(title)
        };

        var body = new StringBuilder();
        body.Append(headerHtml);
        if (!string.IsNullOrEmpty(priorityBadge))
            body.Append(priorityBadge);
        body.Append(H1(E(title)));
        body.Append($"""<p style="margin:0 0 12px;font-size:15px;line-height:1.6;color:#d1d5db;">{content}</p>""");
        body.Append(Divider());
        body.Append($"""<p style="margin:0;font-size:12px;color:#6b7280;">This broadcast was sent to you by the Esportra team.</p>""");

        return (subject, Wrap($"Esportra: {E(title)}", E(title), body.ToString()));
    }

    private static (string HeaderHtml, string AccentColor, string Icon) GetBroadcastTypeStyle(string broadcastType) =>
        broadcastType switch
        {
            "maintenance" => (
                """<div style="background:linear-gradient(135deg,#d97706,#b45309);padding:12px 16px;border-radius:8px;margin-bottom:20px;text-align:center;"><span style="font-size:20px;">🔧</span><span style="color:#fff;font-weight:600;font-size:14px;margin-left:8px;">Scheduled Maintenance</span></div>""",
                "#d97706", "🔧"),
            "promotion" => (
                """<div style="background:linear-gradient(135deg,#7c3aed,#5b21b6);padding:12px 16px;border-radius:8px;margin-bottom:20px;text-align:center;"><span style="font-size:20px;">🎉</span><span style="color:#fff;font-weight:600;font-size:14px;margin-left:8px;">Special Offer</span></div>""",
                "#7c3aed", "🎉"),
            "tournament" => (
                """<div style="background:linear-gradient(135deg,#e11d48,#be123c);padding:12px 16px;border-radius:8px;margin-bottom:20px;text-align:center;"><span style="font-size:20px;">🏆</span><span style="color:#fff;font-weight:600;font-size:14px;margin-left:8px;">Tournament Update</span></div>""",
                "#e11d48", "🏆"),
            "system" => (
                """<div style="background:linear-gradient(135deg,#dc2626,#991b1b);padding:12px 16px;border-radius:8px;margin-bottom:20px;text-align:center;"><span style="font-size:20px;">🛡️</span><span style="color:#fff;font-weight:600;font-size:14px;margin-left:8px;">System Alert</span></div>""",
                "#dc2626", "🛡️"),
            _ => (
                """<div style="background:linear-gradient(135deg,#e11d48,#be123c);padding:12px 16px;border-radius:8px;margin-bottom:20px;text-align:center;"><span style="font-size:20px;">📢</span><span style="color:#fff;font-weight:600;font-size:14px;margin-left:8px;">Announcement</span></div>""",
                "#e11d48", "📢")
        };

    private static string GetPriorityBadge(string priority) =>
        priority switch
        {
            "urgent" => """<div style="margin-bottom:12px;"><span style="background:#dc2626;color:#fff;font-size:11px;font-weight:700;padding:4px 10px;border-radius:4px;text-transform:uppercase;letter-spacing:1px;">Urgent</span></div>""",
            "high" => """<div style="margin-bottom:12px;"><span style="background:#d97706;color:#fff;font-size:11px;font-weight:700;padding:4px 10px;border-radius:4px;text-transform:uppercase;letter-spacing:1px;">High Priority</span></div>""",
            _ => ""
        };
}
