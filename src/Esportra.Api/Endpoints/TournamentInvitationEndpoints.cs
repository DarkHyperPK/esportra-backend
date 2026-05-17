using System.Data;
using System.Net.Mail;
using System.Security.Cryptography;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Esportra.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class TournamentInvitationEndpoints
{
    public static void MapTournamentInvitationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tournaments/{id}/invitations", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, reserved_invite_slots
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                """,
                new { id });
            if (tournament is null) return Results.NotFound();
            if (!await CanManageTournamentAsync(conn, id, userCtx)) return Results.Forbid();

            await conn.ExecuteAsync(
                """
                UPDATE public.tournament_invitations
                SET status = 'expired', updated_at = NOW()
                WHERE tournament_id = @id
                  AND status = 'sent'
                  AND expires_at IS NOT NULL
                  AND expires_at <= NOW()
                """,
                new { id });

            var invitations = (await conn.QueryAsync<dynamic>(
                """
                SELECT ti.id, ti.tournament_id, ti.email, ti.code, ti.status,
                       ti.expires_at, ti.sent_at, ti.redeemed_at, ti.created_at,
                       ti.redeemed_team_id AS team_id,
                       teams.name AS team_name
                FROM public.tournament_invitations ti
                LEFT JOIN public.teams ON teams.id = ti.redeemed_team_id
                WHERE ti.tournament_id = @id
                ORDER BY ti.created_at DESC
                """,
                new { id })).AsList();

            var reservedSlots = Math.Max((int)(tournament.reserved_invite_slots ?? 0), 0);
            var activeSlots = invitations.Count(i => !string.Equals((string)i.status, "revoked", StringComparison.OrdinalIgnoreCase));
            var usedSlots = invitations.Count(i => string.Equals((string)i.status, "redeemed", StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new
            {
                invitations,
                summary = new
                {
                    reservedSlots,
                    activeSlots,
                    usedSlots,
                    remainingSlots = Math.Max(reservedSlots - activeSlots, 0)
                }
            });
        }).WithMetadata(new RateLimitPolicyMetadata("default")).RequireAuthorization("Organizer");

        app.MapPost("/api/tournaments/{id}/invitations/draft", async (
            Guid id,
            [FromBody] DraftInvitesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var emails = (req.Emails ?? [])
                .Select(email => email.Trim().ToLowerInvariant())
                .Where(email => !string.IsNullOrWhiteSpace(email))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (emails.Length == 0) return Results.BadRequest(new { error = "At least one email address is required." });
            if (emails.Length > 100) return Results.BadRequest(new { error = "You can draft up to 100 invitations at a time." });
            if (emails.Any(email => !IsValidEmail(email))) return Results.BadRequest(new { error = "One or more email addresses are invalid." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, reserved_invite_slots
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { id }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(); }
            if (!await CanManageTournamentAsync(conn, id, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }

            var reservedSlots = Math.Max((int)(tournament.reserved_invite_slots ?? 0), 0);
            if (reservedSlots <= 0)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Reserved invite slots are not configured for this tournament." });
            }

            var activeInviteCount = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM public.tournament_invitations
                WHERE tournament_id = @id
                  AND status <> 'revoked'
                """,
                new { id }, tx);
            if (activeInviteCount + emails.Length > reservedSlots)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Not enough reserved invite slots remain." });
            }

            var duplicateEmails = (await conn.QueryAsync<string>(
                """
                SELECT email
                FROM public.tournament_invitations
                WHERE tournament_id = @id
                  AND status <> 'revoked'
                  AND LOWER(email) = ANY(@emails)
                """,
                new { id, emails }, tx)).AsList();
            if (duplicateEmails.Count > 0)
            {
                tx.Rollback();
                return Results.Conflict(new { error = "One or more emails already have active invitations.", emails = duplicateEmails });
            }

            var inviteIds = emails.Select(_ => Guid.NewGuid()).ToArray();
            var codes = await GenerateUniqueInvitationCodesAsync(conn, tx, emails.Length);

            var created = (await conn.QueryAsync<dynamic>(
                """
                WITH input AS (
                    SELECT *
                    FROM unnest(@inviteIds::uuid[], @emails::text[], @codes::text[]) AS x(id, email, code)
                )
                INSERT INTO public.tournament_invitations (id, tournament_id, email, code, status)
                SELECT input.id, @tournamentId, input.email, input.code, 'draft'
                FROM input
                RETURNING id, tournament_id, email, code, status, expires_at, sent_at, redeemed_at, created_at
                """,
                new { tournamentId = id, inviteIds, emails, codes }, tx)).AsList();

            tx.Commit();

            await audit.LogCustomAsync(
                userCtx.UserIdGuid,
                userCtx.Email,
                "tournament_invite_drafted",
                TargetType.Tournament,
                id,
                (string)tournament.name,
                new { invite_ids = inviteIds, emails },
                AuditSeverity.Low,
                ct);

            return Results.Ok(new { invitations = created });
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Organizer");

        app.MapPost("/api/tournaments/{id}/invitations/send", async (
            Guid id,
            [FromBody] SendInvitesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            AuditService audit,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var requestedIds = (req.InvitationIds ?? []).Distinct().ToArray();
            var sendAllDrafts = requestedIds.Length == 0;

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, slug, invite_expiry_days
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { id }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(); }
            if (!await CanManageTournamentAsync(conn, id, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }

            var draftInvites = (await conn.QueryAsync<dynamic>(
                """
                SELECT id, email, code
                FROM public.tournament_invitations
                WHERE tournament_id = @id
                  AND status = 'draft'
                  AND (@sendAllDrafts OR id = ANY(@requestedIds))
                FOR UPDATE
                """,
                new { id, sendAllDrafts, requestedIds }, tx)).AsList();

            if (!sendAllDrafts && draftInvites.Count != requestedIds.Length)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Some invitations were not found or are no longer drafts." });
            }
            if (draftInvites.Count == 0)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "No draft invitations are ready to send." });
            }

            var sendIds = draftInvites.Select(invite => (Guid)invite.id).ToArray();
            var expiryDays = Math.Clamp((int)(tournament.invite_expiry_days ?? 7), 1, 365);
            var updated = (await conn.QueryAsync<dynamic>(
                """
                UPDATE public.tournament_invitations
                SET status = 'sent',
                    sent_at = NOW(),
                    expires_at = NOW() + (@expiryDays * INTERVAL '1 day'),
                    updated_at = NOW()
                WHERE tournament_id = @id
                  AND id = ANY(@sendIds)
                  AND status = 'draft'
                RETURNING id, tournament_id, email, code, status, expires_at, sent_at, redeemed_at, created_at
                """,
                new { id, sendIds, expiryDays }, tx)).AsList();

            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                SELECT p.id,
                       'tournament_invite'::notification_type,
                       'Tournament Invitation',
                       'You have been invited to join ' || @tournamentName || '. Check your email for the invite code.',
                       @link,
                       jsonb_build_object('tournament_id', @tournamentId::text, 'invite_id', ti.id::text),
                       FALSE
                FROM public.tournament_invitations ti
                JOIN public.profiles p ON LOWER(p.email) = LOWER(ti.email)
                WHERE ti.id = ANY(@sendIds)
                """,
                new
                {
                    tournamentId = id,
                    tournamentName = (string)tournament.name,
                    link = $"/tournaments/{((string?)tournament.slug ?? id.ToString())}",
                    sendIds
                },
                tx);

            tx.Commit();

            var frontendUrl = (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');
            var tournamentUrl = $"{frontendUrl}/tournaments/{((string?)tournament.slug ?? id.ToString())}";
            var sentCount = 0;
            foreach (var invite in updated)
            {
                try
                {
                    await email.SendAsync((string)invite.email, EmailType.TournamentInvite, new
                    {
                        captainName = "Captain",
                        tournamentName = (string)tournament.name,
                        code = (string)invite.code,
                        tournamentUrl,
                        expiryDate = ((DateTime)invite.expires_at).ToString("MMM dd, yyyy")
                    }, ct);
                    sentCount++;
                }
                catch { /* best effort; DB state already records the send attempt */ }
            }

            await audit.LogCustomAsync(
                userCtx.UserIdGuid,
                userCtx.Email,
                "tournament_invite_sent",
                TargetType.Tournament,
                id,
                (string)tournament.name,
                new { invite_ids = sendIds, count = sentCount, failed_count = updated.Count - sentCount },
                AuditSeverity.Low,
                ct);

            return Results.Ok(new { sent_count = sentCount, failed_count = updated.Count - sentCount, invitations = updated });
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Organizer");

        app.MapDelete("/api/invitations/{inviteId}", async (
            Guid inviteId,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ti.id, ti.tournament_id, ti.email, ti.status,
                       t.name AS tournament_name
                FROM public.tournament_invitations ti
                JOIN public.tournaments t ON t.id = ti.tournament_id
                WHERE ti.id = @inviteId
                FOR UPDATE OF ti
                """,
                new { inviteId }, tx);
            if (invite is null) { tx.Rollback(); return Results.NotFound(); }

            var tournamentId = (Guid)invite.tournament_id;
            if (!await CanManageTournamentAsync(conn, tournamentId, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }
            if (string.Equals((string)invite.status, "redeemed", StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Redeemed invitations cannot be revoked." });
            }

            await conn.ExecuteAsync(
                """
                UPDATE public.tournament_invitations
                SET status = 'revoked', updated_at = NOW()
                WHERE id = @inviteId
                """,
                new { inviteId }, tx);

            tx.Commit();

            await audit.LogCustomAsync(
                userCtx.UserIdGuid,
                userCtx.Email,
                "tournament_invite_revoked",
                TargetType.Tournament,
                tournamentId,
                (string)invite.tournament_name,
                new { invite_id = inviteId, email = (string)invite.email },
                AuditSeverity.Medium,
                ct);

            return Results.NoContent();
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Organizer");

        app.MapPost("/api/invitations/redeem", async (
            [FromBody] RedeemInviteRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { error = "Invitation code is required." });
            if (string.IsNullOrWhiteSpace(userCtx.Email)) return Results.BadRequest(new { error = "Your account email could not be verified." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ti.id, ti.tournament_id, ti.email, ti.status, ti.expires_at,
                       t.name AS tournament_name
                FROM public.tournament_invitations ti
                JOIN public.tournaments t ON t.id = ti.tournament_id
                WHERE ti.code = @code
                FOR UPDATE OF ti
                """,
                new { code }, tx);
            if (invite is null) { tx.Rollback(); return Results.NotFound(new { error = "Invitation code was not found." }); }

            var tournamentId = (Guid)invite.tournament_id;
            if (!string.Equals((string)invite.status, "sent", StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Invitation code is not active." });
            }

            var expiresAt = (DateTime?)invite.expires_at;
            if (expiresAt is null || expiresAt <= DateTime.UtcNow)
            {
                await conn.ExecuteAsync(
                    "UPDATE public.tournament_invitations SET status = 'expired', updated_at = NOW() WHERE id = @id",
                    new { id = (Guid)invite.id }, tx);
                tx.Commit();
                return Results.BadRequest(new { error = "Invitation code has expired." });
            }

            if (!string.Equals(((string)invite.email).Trim(), userCtx.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return Results.Forbid();
            }

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, status, max_teams
                FROM public.tournaments
                WHERE id = @tournamentId AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { tournamentId }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(new { error = "Tournament was not found." }); }
            if ((string)tournament.status is not "open" and not "published")
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Tournament is not accepting registrations." });
            }

            var captainTeam = req.TeamId.HasValue
                ? await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT tm.team_id, teams.name AS team_name
                    FROM public.team_members tm
                    JOIN public.teams ON teams.id = tm.team_id
                    WHERE tm.user_id = @userId
                      AND tm.team_id = @teamId
                      AND tm.role = 'captain'
                      AND tm.is_active = TRUE
                    LIMIT 1
                    """,
                    new { userId = userCtx.UserIdGuid, teamId = req.TeamId.Value }, tx)
                : await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT tm.team_id, teams.name AS team_name
                    FROM public.team_members tm
                    JOIN public.teams ON teams.id = tm.team_id
                    WHERE tm.user_id = @userId
                      AND tm.role = 'captain'
                      AND tm.is_active = TRUE
                    ORDER BY teams.created_at ASC
                    LIMIT 1
                    """,
                    new { userId = userCtx.UserIdGuid }, tx);

            if (captainTeam is null)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "You must be the captain of an active team to redeem this invitation." });
            }

            var teamId = (Guid)captainTeam.team_id;
            var alreadyRegistered = await conn.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM public.tournament_participants
                    WHERE tournament_id = @tournamentId
                      AND status NOT IN ('cancelled', 'rejected', 'disqualified')
                      AND (user_id = @userId OR team_captain_id = @userId OR team_id = @teamId)
                )
                """,
                new { tournamentId, userId = userCtx.UserIdGuid, teamId }, tx);
            if (alreadyRegistered)
            {
                tx.Rollback();
                return Results.Conflict(new { error = "Your team is already registered for this tournament." });
            }

            int? maxTeams = (int?)tournament.max_teams;
            if (maxTeams.HasValue && maxTeams.Value > 0)
            {
                var currentCount = await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM public.tournament_participants WHERE tournament_id = @tournamentId AND status NOT IN ('rejected', 'cancelled')",
                    new { tournamentId }, tx);
                if (currentCount >= maxTeams.Value)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "Tournament has reached maximum capacity." });
                }
            }
            // Lock tournament row to prevent race conditions on capacity
            await conn.ExecuteAsync(
                "SELECT 1 FROM public.tournaments WHERE id = @tournamentId FOR UPDATE",
                new { tournamentId }, tx);

            Guid? seasonId = await TryAutoEnrollSeasonAsync(conn, tx, tournamentId, teamId);

            var participant = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO public.tournament_participants
                    (tournament_id, user_id, team_id, team_captain_id, team_name,
                     team_members, team_contact_email, status, participant_type, source,
                     entry_fee_amount, entry_fee_paid, payment_status)
                VALUES
                    (@tournamentId, @userId, @teamId, @userId, @teamName,
                     '[]'::jsonb, @email, 'approved'::registration_status, 'team'::registration_type, 'invite',
                     0, TRUE, 'not_required')
                RETURNING id, tournament_id, user_id, team_id, team_captain_id, team_name,
                          status, participant_type, source, created_at
                """,
                new
                {
                    tournamentId,
                    userId = userCtx.UserIdGuid,
                    teamId,
                    teamName = (string)captainTeam.team_name,
                    email = userCtx.Email
                }, tx);

            await conn.ExecuteAsync(
                """
                UPDATE public.tournament_invitations
                SET status = 'redeemed',
                    redeemed_by = @userId,
                    redeemed_team_id = @teamId,
                    redeemed_at = NOW(),
                    updated_at = NOW()
                WHERE id = @inviteId
                """,
                new { userId = userCtx.UserIdGuid, teamId, inviteId = (Guid)invite.id }, tx);

            tx.Commit();

            try
            {
                await cache.RemoveByTagAsync("tournament-list", ct);
                if (seasonId.HasValue) await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, seasonId.Value, ct);
            }
            catch { }

            await audit.LogCustomAsync(
                userCtx.UserIdGuid,
                userCtx.Email,
                "tournament_invite_redeemed",
                TargetType.Tournament,
                tournamentId,
                (string)tournament.name,
                new { invite_id = (Guid)invite.id, team_id = teamId, user_id = userCtx.UserIdGuid, season_id = seasonId },
                AuditSeverity.Medium,
                ct);

            return Results.Ok(new { success = true, tournamentId, seasonId, participant });
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/invitations/resend ────────────────────────
        // Resend invitations that are 'sent' (refresh expiry + re-email)
        app.MapPost("/api/tournaments/{id}/invitations/resend", async (
            Guid id,
            [FromBody] ResendInvitesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            AuditService audit,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var requestedIds = (req.InvitationIds ?? []).Distinct().ToArray();
            if (requestedIds.Length == 0) return Results.BadRequest(new { error = "At least one invitation ID is required." });
            if (requestedIds.Length > 100) return Results.BadRequest(new { error = "Cannot resend more than 100 invitations at a time." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, slug, invite_expiry_days
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { id }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(); }
            if (!await CanManageTournamentAsync(conn, id, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }

            var expiryDays = Math.Clamp((int)(tournament.invite_expiry_days ?? 7), 1, 365);
            var updated = (await conn.QueryAsync<dynamic>(
                """
                UPDATE public.tournament_invitations
                SET status = 'sent',
                    sent_at = NOW(),
                    expires_at = NOW() + (@expiryDays * INTERVAL '1 day'),
                    updated_at = NOW()
                WHERE tournament_id = @id
                  AND id = ANY(@requestedIds)
                  AND status IN ('sent', 'expired')
                RETURNING id, tournament_id, email, code, status, expires_at, sent_at, redeemed_at, created_at
                """,
                new { id, requestedIds, expiryDays }, tx)).AsList();

            if (updated.Count == 0) { tx.Rollback(); return Results.BadRequest(new { error = "No eligible invitations found to resend." }); }

            tx.Commit();

            // Re-send emails
            var frontendUrl = (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');
            var tournamentUrl = $"{frontendUrl}/tournaments/{((string?)tournament.slug ?? id.ToString())}";
            var sentCount = 0;
            foreach (var invite in updated)
            {
                try
                {
                    await email.SendAsync((string)invite.email, EmailType.TournamentInvite, new
                    {
                        captainName = "Captain",
                        tournamentName = (string)tournament.name,
                        code = (string)invite.code,
                        tournamentUrl,
                        expiryDate = ((DateTime)invite.expires_at).ToString("MMM dd, yyyy")
                    }, ct);
                    sentCount++;
                }
                catch { /* best effort */ }
            }

            await audit.LogCustomAsync(
                userCtx.UserIdGuid, userCtx.Email,
                "tournament_invite_resent",
                TargetType.Tournament, id, (string)tournament.name,
                new { invite_ids = updated.Select(u => (Guid)u.id).ToArray(), count = sentCount },
                AuditSeverity.Low, ct);

            return Results.Ok(new { resent_count = sentCount, invitations = updated });
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Organizer");

        // ── POST /api/tournaments/{id}/invitations/import-csv ────────────────────
        // Bulk import emails from CSV text (one email per line or comma-separated)
        app.MapPost("/api/tournaments/{id}/invitations/import-csv", async (
            Guid id,
            [FromBody] CsvImportRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.CsvContent))
                return Results.BadRequest(new { error = "CSV content is required." });

            // Parse emails from CSV: support comma, semicolon, newline delimiters
            var emails = req.CsvContent
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .Where(e => !string.IsNullOrWhiteSpace(e) && IsValidEmail(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (emails.Length == 0) return Results.BadRequest(new { error = "No valid email addresses found in CSV content." });
            if (emails.Length > 500) return Results.BadRequest(new { error = "Cannot import more than 500 emails at once." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, reserved_invite_slots
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { id }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(); }
            if (!await CanManageTournamentAsync(conn, id, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }

            var reservedSlots = Math.Max((int)(tournament.reserved_invite_slots ?? 0), 0);
            if (reservedSlots <= 0) { tx.Rollback(); return Results.BadRequest(new { error = "Reserved invite slots are not configured." }); }

            var activeInviteCount = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM public.tournament_invitations WHERE tournament_id = @id AND status <> 'revoked'",
                new { id }, tx);

            // Filter out already-invited emails
            var existingEmails = (await conn.QueryAsync<string>(
                """
                SELECT LOWER(email)
                FROM public.tournament_invitations
                WHERE tournament_id = @id AND status <> 'revoked' AND LOWER(email) = ANY(@emails)
                """,
                new { id, emails }, tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newEmails = emails.Where(e => !existingEmails.Contains(e)).ToArray();
            if (newEmails.Length == 0) { tx.Rollback(); return Results.Ok(new { imported = 0, skipped = emails.Length, reason = "All emails already have active invitations." }); }

            var available = reservedSlots - activeInviteCount;
            if (available <= 0) { tx.Rollback(); return Results.BadRequest(new { error = "No invite slots remaining." }); }

            // Cap to available slots
            var toImport = newEmails.Take(available).ToArray();
            var inviteIds = toImport.Select(_ => Guid.NewGuid()).ToArray();
            var codes = await GenerateUniqueInvitationCodesAsync(conn, tx, toImport.Length);

            await conn.ExecuteAsync(
                """
                WITH input AS (
                    SELECT *
                    FROM unnest(@inviteIds::uuid[], @emails::text[], @codes::text[]) AS x(id, email, code)
                )
                INSERT INTO public.tournament_invitations (id, tournament_id, email, code, status)
                SELECT input.id, @tournamentId, input.email, input.code, 'draft'
                FROM input
                """,
                new { tournamentId = id, inviteIds, emails = toImport, codes }, tx);

            tx.Commit();

            await audit.LogCustomAsync(
                userCtx.UserIdGuid, userCtx.Email,
                "tournament_invite_csv_import",
                TargetType.Tournament, id, (string)tournament.name,
                new { imported = toImport.Length, skipped = existingEmails.Count, total_parsed = emails.Length },
                AuditSeverity.Low, ct);

            return Results.Ok(new
            {
                imported = toImport.Length,
                skipped = existingEmails.Count,
                capped = Math.Max(newEmails.Length - available, 0),
                inviteIds
            });
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{id}/invitations/stats ──────────────────────────
        // Invitation analytics: breakdown by status, response rates, timing
        app.MapGet("/api/tournaments/{id}/invitations/stats", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            if (!await CanManageTournamentAsync(conn, id, userCtx)) return Results.Forbid();

            var stats = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    COUNT(*)::int AS total_invitations,
                    COUNT(*) FILTER (WHERE status = 'draft')::int AS draft_count,
                    COUNT(*) FILTER (WHERE status = 'sent')::int AS sent_count,
                    COUNT(*) FILTER (WHERE status = 'redeemed')::int AS redeemed_count,
                    COUNT(*) FILTER (WHERE status = 'expired')::int AS expired_count,
                    COUNT(*) FILTER (WHERE status = 'revoked')::int AS revoked_count,
                    CASE WHEN COUNT(*) FILTER (WHERE status IN ('sent','redeemed','expired')) > 0
                         THEN ROUND(100.0 * COUNT(*) FILTER (WHERE status = 'redeemed') /
                              COUNT(*) FILTER (WHERE status IN ('sent','redeemed','expired')), 1)
                         ELSE 0 END AS redemption_rate_pct,
                    MIN(sent_at) AS first_sent_at,
                    MAX(sent_at) AS last_sent_at,
                    AVG(EXTRACT(EPOCH FROM (redeemed_at - sent_at)) / 3600)
                        FILTER (WHERE redeemed_at IS NOT NULL AND sent_at IS NOT NULL) AS avg_redemption_hours
                FROM public.tournament_invitations
                WHERE tournament_id = @id
                """,
                new { id });

            var reservedSlots = await conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT reserved_invite_slots FROM public.tournaments WHERE id = @id",
                new { id });

            return Results.Ok(new
            {
                stats,
                reserved_slots = reservedSlots ?? 0,
            });
        }).RequireAuthorization("Organizer");
    }

    private static async Task<bool> CanManageTournamentAsync(IDbConnection conn, Guid tournamentId, UserContext userCtx, IDbTransaction? tx = null)
    {
        if (StaffAuthHelper.IsPlatformAdmin(userCtx)) return true;
        return await StaffAuthHelper.CanActOnTournamentAsync(
            conn,
            userCtx.UserIdGuid,
            tournamentId,
            StaffAuthHelper.PermTeamsManage,
            tx);
    }

    private static bool IsValidEmail(string email)
    {
        try { _ = new MailAddress(email); return true; }
        catch { return false; }
    }

    private static async Task<string[]> GenerateUniqueInvitationCodesAsync(IDbConnection conn, IDbTransaction tx, int count)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (codes.Count < count)
            codes.Add(GenerateInvitationCode());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var existing = (await conn.QueryAsync<string>(
                """
                SELECT code
                FROM public.tournament_invitations
                WHERE LOWER(code) = ANY(@lowerCodes)
                """,
                new { lowerCodes = codes.Select(code => code.ToLowerInvariant()).ToArray() }, tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existing.Count == 0) return codes.ToArray();

            codes.ExceptWith(existing);
            while (codes.Count < count)
                codes.Add(GenerateInvitationCode());
        }

        throw new InvalidOperationException("Unable to generate unique invitation codes.");
    }

    private static string GenerateInvitationCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 4) { chars[i] = '-'; continue; }
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(chars);
    }

    private static async Task<Guid?> TryAutoEnrollSeasonAsync(IDbConnection conn, IDbTransaction tx, Guid tournamentId, Guid teamId)
    {
        var hasSeasonTables = await conn.QuerySingleAsync<bool>(
            """
            SELECT to_regclass('public.season_tournaments') IS NOT NULL
               AND to_regclass('public.season_participants') IS NOT NULL
            """,
            transaction: tx);
        if (!hasSeasonTables) return null;

        var seasonId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT season_id FROM public.season_tournaments WHERE tournament_id = @tournamentId LIMIT 1",
            new { tournamentId }, tx);
        if (seasonId is null) return null;

        var team = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT name, logo_url, slug FROM public.teams WHERE id = @teamId",
            new { teamId }, tx);
        if (team is null) return seasonId;

        await conn.ExecuteAsync(
            """
            INSERT INTO public.season_participants
                (season_id, team_id, team_name, team_logo_url, team_slug, status, registered_by)
            VALUES
                (@seasonId, @teamId, @teamName, @teamLogoUrl, @teamSlug, 'approved', NULL)
            ON CONFLICT (season_id, team_id) DO UPDATE SET
                status = CASE
                    WHEN public.season_participants.status = 'rejected' THEN 'approved'
                    ELSE public.season_participants.status
                END,
                updated_at = NOW()
            """,
            new
            {
                seasonId,
                teamId,
                teamName = (string)team.name,
                teamLogoUrl = (string?)team.logo_url,
                teamSlug = (string?)team.slug
            }, tx);

        return seasonId;
    }
}

public sealed record DraftInvitesRequest(List<string>? Emails = null);
public sealed record SendInvitesRequest(Guid[]? InvitationIds = null);
public sealed record RedeemInviteRequest(string Code, Guid? TeamId = null);
public sealed record ResendInvitesRequest(Guid[]? InvitationIds = null);
public sealed record CsvImportRequest(string? CsvContent = null);

