using System.Text;

namespace Esportra.Infrastructure.Email;

/// <summary>
/// Produces branded HTML email bodies. Ported from supabase/functions/send-email/templates.ts.
/// Dark theme: #0a0a0a base, #e11d48 rose CTA.
/// </summary>
public static class EmailTemplates
{
    // ── Base wrapper ──────────────────────────────────────────────────────────

    private static string Wrap(string preheader, string subject, string body) => $"""
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
                <tr>
                  <td style="background:linear-gradient(135deg,#e11d48,#be123c);padding:24px 32px;text-align:center;">
                    <span style="font-size:24px;font-weight:700;color:#ffffff;letter-spacing:-0.5px;">⚡ Esportra</span>
                  </td>
                </tr>
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
        $"""<a href="{href}" style="display:inline-block;background:linear-gradient(135deg,#e11d48,#be123c);color:#fff;font-weight:600;font-size:14px;padding:12px 28px;border-radius:8px;text-decoration:none;margin-top:16px;">{label}</a>""";

    private static string H1(string text) =>
        $"""<h1 style="margin:0 0 16px;font-size:22px;font-weight:700;color:#f9fafb;">{text}</h1>""";

    private static string P(string text) =>
        $"""<p style="margin:0 0 12px;font-size:15px;line-height:1.6;color:#d1d5db;">{text}</p>""";

    // ── Templates ─────────────────────────────────────────────────────────────

    public static (string Subject, string Html) Welcome(string username) =>
    (
        "Welcome to Esportra!",
        Wrap("Your competitive journey starts here.", "Welcome to Esportra", $"""
            {H1($"Welcome, {username}!")}
            {P("You've joined the premier esports tournament platform. Start by joining or creating a team, then register for upcoming tournaments.")}
            {Btn("https://esportra.com/tournaments", "Browse Tournaments")}
        """)
    );

    public static (string Subject, string Html) TournamentRegistration(
        string username, string tournamentName, string startDate, string tournamentUrl) =>
    (
        $"You're registered for {tournamentName}",
        Wrap($"You're in for {tournamentName}", "Tournament Registration", $"""
            {H1("Registration Confirmed!")}
            {P($"Hi {username}, you've successfully registered for <strong style='color:#f9fafb;'>{tournamentName}</strong>.")}
            {P($"<strong style='color:#f9fafb;'>Start Date:</strong> {startDate}")}
            {P("Keep an eye on your notifications for check-in reminders and match schedules.")}
            {Btn(tournamentUrl, "View Tournament")}
        """)
    );

    public static (string Subject, string Html) CheckinReminder(
        string username, string tournamentName, string checkInUrl) =>
    (
        $"⚠️ Check-in now open: {tournamentName}",
        Wrap($"Check in now for {tournamentName}", "Check-in Reminder", $"""
            {H1("Check-in Window Open!")}
            {P($"Hi {username}, the check-in window for <strong style='color:#f9fafb;'>{tournamentName}</strong> is now open.")}
            {P("<strong style='color:#ef4444;'>You must check in or you'll be removed from the tournament.</strong>")}
            {Btn(checkInUrl, "Check In Now")}
        """)
    );

    public static (string Subject, string Html) TeamInvite(
        string inviteeName, string teamName, string captainName, string acceptUrl) =>
    (
        $"You've been invited to join {teamName}",
        Wrap($"Team invite from {captainName}", "Team Invitation", $"""
            {H1("Team Invitation")}
            {P($"Hi {inviteeName}, <strong style='color:#f9fafb;'>{captainName}</strong> has invited you to join <strong style='color:#f9fafb;'>{teamName}</strong>.")}
            {Btn(acceptUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) StaffInvite(
        string inviteeName, string orgName, string role, string permissions, string acceptUrl) =>
    (
        $"Staff invitation: {orgName}",
        Wrap($"You've been invited to staff {orgName}", "Staff Invitation", $"""
            {H1("Staff Invitation")}
            {P($"Hi {inviteeName}, you've been invited to join <strong style='color:#f9fafb;'>{orgName}</strong> as <strong style='color:#f9fafb;'>{role}</strong>.")}
            {P($"<strong style='color:#f9fafb;'>Permissions:</strong> {permissions}")}
            {Btn(acceptUrl, "Accept Invitation")}
        """)
    );

    public static (string Subject, string Html) PartnerInvite(
        string sponsorName, string setupUrl) =>
    (
        $"Partner Portal access: {sponsorName}",
        Wrap("Set up your partner account", "Partner Portal Access", $"""
            {H1($"Welcome to the {sponsorName} Partner Portal")}
            {P("You've been invited to manage your sponsor account on Esportra.")}
            {P("Click below to set up your password and access the portal.")}
            {Btn(setupUrl, "Set Up Account")}
        """)
    );

    public static (string Subject, string Html) PartnerWelcome(
        string sponsorName, string portalUrl) =>
    (
        $"Partner Portal access: {sponsorName}",
        Wrap("Your partner account is ready", "Partner Portal Access", $"""
            {H1($"Welcome back, {sponsorName} partner!")}
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
}
