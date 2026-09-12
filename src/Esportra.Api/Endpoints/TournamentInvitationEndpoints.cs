using System.Data;
using System.Net.Mail;
using System.Security.Cryptography;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Esportra.Core.Tournaments;
using Esportra.Api.Services;
using Esportra.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;
using System.Text.Json;

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
                SELECT id, name, reserved_invite_slots, settings
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                """,
                new { id });
            if (tournament is null) return Results.NotFound();
            if (!await CanManageTournamentAsync(conn, id, userCtx)) return Results.Forbid();

            var invitations = (await conn.QueryAsync<dynamic>(
                """
                SELECT ti.id, ti.tournament_id, ti.email, ti.code,
                       CASE WHEN ti.status = 'sent' AND ti.expires_at < NOW() THEN 'expired' ELSE ti.status END AS status,
                       ti.expires_at, ti.sent_at, ti.redeemed_at, ti.created_at,
                       COALESCE(ti.redeemed_team_id, ti.redeemed_participant_id) AS team_id,
                       COALESCE(teams.name, solo_participant.team_name) AS team_name
                FROM public.tournament_invitations ti
                LEFT JOIN public.teams ON teams.id = ti.redeemed_team_id
                LEFT JOIN public.tournament_participants solo_participant
                    ON solo_participant.id = ti.redeemed_participant_id
                WHERE ti.tournament_id = @id
                ORDER BY ti.created_at DESC
                """,
                new { id })).AsList();

            var reservedSlots = TournamentInviteSlots.ResolveFromRow(tournament);
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
        }).WithMetadata(new RateLimitPolicyMetadata("default")).RequireAuthorization("Authenticated");

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
                SELECT id, name, reserved_invite_slots, settings
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { id }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(); }
            if (!await CanManageTournamentAsync(conn, id, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }

            var reservedSlots = TournamentInviteSlots.ResolveFromRow(tournament);
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
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

        app.MapPost("/api/tournaments/{id}/invitations/send", async (
            Guid id,
            [FromBody] SendInvitesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            GameCatalogService catalog,
            IHubContext<NotificationHub> notifHub,
            AuditService audit,
            DiscordNotificationService discord,
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
                SELECT id, name, slug, game, invite_expiry_days
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

            var tournamentName = (string)tournament.name;
            var tournamentGame = (string?)tournament.game;
            var tournamentLink = $"/tournaments/{((string?)tournament.slug ?? id.ToString())}";

            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                SELECT p.id,
                       'tournament_invite'::notification_type,
                       'You''re invited to ' || @tournamentName,
                       'Your invite code is ' || ti.code || '. Redeem it to claim your reserved slot.',
                       @link,
                       jsonb_build_object(
                           'tournament_id', @tournamentId::text,
                           'invite_id', ti.id::text,
                           'code', ti.code,
                           'tournament_name', @tournamentName,
                           'game', @game,
                           'expires_at', ti.expires_at
                       ),
                       FALSE
                FROM public.tournament_invitations ti
                JOIN public.profiles p ON LOWER(p.email) = LOWER(ti.email)
                WHERE ti.id = ANY(@sendIds)
                """,
                new
                {
                    tournamentId = id,
                    tournamentName,
                    game = tournamentGame ?? "",
                    link = tournamentLink,
                    sendIds
                },
                tx);

            tx.Commit();

            var gameHeaderUrl = await catalog.ResolveEmailBannerUrlAsync(tournamentGame ?? "", ct) ?? "";

            var pushedNotifications = (await conn.QueryAsync<dynamic>(
                """
                SELECT n.id, n.user_id, n.title, n.message, n.link, n.data
                FROM public.notifications n
                WHERE n.type = 'tournament_invite'
                  AND (n.data->>'invite_id')::uuid = ANY(@sendIds)
                """,
                new { sendIds })).AsList();

            foreach (var notification in pushedNotifications)
            {
                var userId = ((Guid)notification.user_id).ToString();
                await notifHub.Clients
                    .Group(NotificationHub.UserGroup(userId))
                    .SendAsync(
                        NotificationHubEvents.NewNotification,
                        new
                        {
                            id = ((Guid)notification.id).ToString(),
                            type = "tournament_invite",
                            title = (string)notification.title,
                            message = (string)notification.message,
                            link = (string?)notification.link,
                            data = notification.data,
                        },
                        ct);
            }

            var tournamentInviteUrlBase = config["FrontendUrl"] ?? "https://esportra.com";
            foreach (var notification in pushedNotifications)
            {
                var tournamentInviteLink = (string?)notification.link;
                var dmTournamentMsg = string.IsNullOrWhiteSpace(tournamentInviteLink)
                    ? "You've been invited to join a tournament."
                    : $"You've been invited to join a tournament.\n\n[View →]({tournamentInviteUrlBase}{tournamentInviteLink})";
                await discord.TrySendDmAsync(
                    (Guid)notification.user_id,
                    "tournament_invite",
                    "Tournament Invitation",
                    dmTournamentMsg);
            }

            var sentCount = 0;
            foreach (var invite in updated)
            {
                try
                {
                    var redeemUrl = BuildInviteRedeemUrl(
                        config,
                        (string)invite.code,
                        (string?)tournament.slug,
                        id);
                    await email.SendAsync((string)invite.email, EmailType.TournamentInvite, new
                    {
                        captainName = "Captain",
                        tournamentName,
                        code = (string)invite.code,
                        tournamentUrl = redeemUrl,
                        expiryDate = ((DateTime)invite.expires_at).ToString("MMM dd, yyyy"),
                        gameHeaderUrl,
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
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

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
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

        app.MapGet("/api/invitations/preview", async (
            string? code,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var normalizedCode = NormalizeInvitationCode(code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                return Results.BadRequest(new { error = "Invitation code is required." });

            using var conn = db.CreateConnection();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ti.id, ti.tournament_id, ti.email, ti.status, ti.expires_at,
                       t.name AS tournament_name, t.slug AS tournament_slug, t.game,
                       t.game_mode, t.team_size, t.status AS tournament_status,
                       t.registration_deadline, t.start_date, t.settings
                FROM public.tournament_invitations ti
                JOIN public.tournaments t ON t.id = ti.tournament_id
                WHERE ti.code = @code
                """,
                new { code = normalizedCode });

            if (invite is null)
            {
                return Results.Ok(new
                {
                    canRedeem = false,
                    emailMatch = false,
                    status = "not_found",
                    message = "Invitation code was not found."
                });
            }

            var emailMatch = string.Equals(
                ((string)invite.email).Trim(),
                userCtx.Email.Trim(),
                StringComparison.OrdinalIgnoreCase);

            if (!emailMatch)
            {
                return Results.Ok(new
                {
                    canRedeem = false,
                    emailMatch = false,
                    status = (string)invite.status,
                    message = "This code is locked to another email address."
                });
            }

            var status = ((string)invite.status).ToLowerInvariant();
            var expiresAt = (DateTime?)invite.expires_at;
            var isExpired = expiresAt is null || expiresAt <= DateTime.UtcNow;
            var tournamentStatus = ((string)invite.tournament_status).ToLowerInvariant();
            var registrationOpens = TournamentTimelineValidator.ParseRegistrationOpensAt(invite.settings);
            var registrationWindowError = TournamentTimelineValidator.ValidateRegistrationWindow(
                tournamentStatus,
                (DateTimeOffset?)invite.registration_deadline,
                (DateTimeOffset?)invite.start_date,
                registrationOpens);

            string? message = null;
            var canRedeem = status == "sent" && !isExpired && registrationWindowError is null;
            if (status == "redeemed") message = "This invitation has already been used.";
            else if (status == "revoked") message = "This invitation was revoked by the organizer.";
            else if (status == "draft") message = "This invitation has not been sent yet.";
            else if (isExpired) message = "This invitation has expired. Ask the organizer to resend it.";
            else if (registrationWindowError is not null) message = registrationWindowError;

            return Results.Ok(new
            {
                canRedeem,
                emailMatch = true,
                status,
                tournamentId = (Guid)invite.tournament_id,
                tournamentSlug = (string?)invite.tournament_slug,
                tournamentName = (string)invite.tournament_name,
                tournamentGame = (string?)invite.game,
                tournamentGameMode = (string?)invite.game_mode,
                tournamentTeamSize = (int?)invite.team_size,
                tournamentStatus = (string)invite.tournament_status,
                expiresAt,
                message
            });
        }).WithMetadata(new RateLimitPolicyMetadata("default")).RequireAuthorization("Authenticated");

        app.MapPost("/api/invitations/redeem", async (
            [FromBody] RedeemInviteRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService gameCatalog,
            HybridCache cache,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var code = NormalizeInvitationCode(req.Code);
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { error = "Invitation code is required." });
            if (string.IsNullOrWhiteSpace(userCtx.Email)) return Results.BadRequest(new { error = "Your account email could not be verified." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            try
            {
                var inviteResult = await ValidateInviteAsync(conn, tx, code);
                if (inviteResult.Error is not null) { tx.Rollback(); return inviteResult.Error; }

                var invite = inviteResult.Invite!;
                var expiresAt = (DateTime?)invite.expires_at;
                if (IsInviteExpired(expiresAt))
                {
                    await conn.ExecuteAsync(
                        "UPDATE public.tournament_invitations SET status = 'expired', updated_at = NOW() WHERE id = @id",
                        new { id = (Guid)invite.id }, tx);
                    tx.Commit();
                    return Results.BadRequest(new { error = "Invitation code has expired." });
                }

                if (!string.Equals(((string)invite.email).Trim(), userCtx.Email!.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return Results.Forbid();
                }

                var tournamentResult = await ValidateTournamentStateAsync(conn, tx, inviteResult.TournamentId, req, userCtx, gameCatalog);
                if (tournamentResult.Error is not null) { tx.Rollback(); return tournamentResult.Error; }

                var tournament = tournamentResult.Tournament!;
                var capacityError = await ValidateCapacityAndDuplicateAsync(
                    conn, tx, inviteResult.TournamentId, userCtx,
                    (int?)tournament.max_teams, tournamentResult.IsSoloTournament, req.TeamId);
                if (capacityError is not null) { tx.Rollback(); return capacityError; }

                dynamic participant;
                Guid? redeemedTeamId = null;
                Guid? redeemedParticipantId = null;
                Guid? rosterId = null;

                if (tournamentResult.IsSoloTournament)
                {
                    participant = await RegisterSoloParticipantAsync(conn, tx, inviteResult.TournamentId, userCtx);
                    redeemedParticipantId = (Guid)participant.id;
                }
                else
                {
                    var teamReg = await RegisterTeamParticipantAsync(conn, tx, inviteResult.TournamentId, req, userCtx, gameCatalog, tournamentResult.TeamMeta!, tournamentResult.RosterMeta!);
                    if (teamReg.Error is not null) { tx.Rollback(); return teamReg.Error; }
                    participant = teamReg.Participant!;
                    redeemedTeamId = teamReg.TeamId;
                    rosterId = teamReg.RosterId;
                }

                await conn.ExecuteAsync(
                    """
                    UPDATE public.tournament_invitations
                    SET status = 'redeemed',
                        redeemed_by = @userId,
                        redeemed_team_id = @teamId,
                        redeemed_participant_id = @participantId,
                        redeemed_at = NOW(),
                        updated_at = NOW()
                    WHERE id = @inviteId
                    """,
                    new
                    {
                        userId = userCtx.UserIdGuid,
                        teamId = redeemedTeamId,
                        participantId = redeemedParticipantId,
                        inviteId = (Guid)invite.id,
                    }, tx);

                tx.Commit();

                try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { }

                await audit.LogCustomAsync(
                    userCtx.UserIdGuid,
                    userCtx.Email,
                    "tournament_invite_redeemed",
                    TargetType.Tournament,
                    inviteResult.TournamentId,
                    (string)tournament.name,
                    new { invite_id = (Guid)invite.id, team_id = redeemedTeamId, participant_id = redeemedParticipantId, roster_id = rosterId, user_id = userCtx.UserIdGuid },
                    AuditSeverity.Medium,
                    ct);

                return Results.Ok(new
                {
                    success = true,
                    tournamentId = inviteResult.TournamentId,
                    tournamentSlug = (string?)tournament.slug ?? (string?)invite.tournament_slug,
                    tournamentName = (string)tournament.name,
                    participant
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/invitations/resend ────────────────────────
        // Resend invitations that are 'sent' (refresh expiry + re-email)
        app.MapPost("/api/tournaments/{id}/invitations/resend", async (
            Guid id,
            [FromBody] ResendInvitesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            GameCatalogService catalog,
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
                SELECT id, name, slug, game, invite_expiry_days
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

            var resendGame = (string?)tournament.game ?? "";
            var gameHeaderUrl = await catalog.ResolveEmailBannerUrlAsync(resendGame, ct) ?? "";

            // Re-send emails
            var sentCount = 0;
            foreach (var invite in updated)
            {
                try
                {
                    var redeemUrl = BuildInviteRedeemUrl(
                        config,
                        (string)invite.code,
                        (string?)tournament.slug,
                        id);
                    await email.SendAsync((string)invite.email, EmailType.TournamentInvite, new
                    {
                        captainName = "Captain",
                        tournamentName = (string)tournament.name,
                        code = (string)invite.code,
                        tournamentUrl = redeemUrl,
                        expiryDate = ((DateTime)invite.expires_at).ToString("MMM dd, yyyy"),
                        gameHeaderUrl,
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
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

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
                SELECT id, name, reserved_invite_slots, settings
                FROM public.tournaments
                WHERE id = @id AND deleted_at IS NULL
                FOR UPDATE
                """,
                new { id }, tx);
            if (tournament is null) { tx.Rollback(); return Results.NotFound(); }
            if (!await CanManageTournamentAsync(conn, id, userCtx, tx)) { tx.Rollback(); return Results.Forbid(); }

            var reservedSlots = TournamentInviteSlots.ResolveFromRow(tournament);
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

            int availableSlots = reservedSlots - activeInviteCount;
            if (availableSlots <= 0) { tx.Rollback(); return Results.BadRequest(new { error = "No invite slots remaining." }); }

            // Cap to available slots
            string[] toImport = newEmails.Take(availableSlots).ToArray();
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
                capped = Math.Max(newEmails.Length - availableSlots, 0),
                inviteIds
            });
        }).WithMetadata(new RateLimitPolicyMetadata("strict")).RequireAuthorization("Authenticated");

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

            var tournamentRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT reserved_invite_slots, settings FROM public.tournaments WHERE id = @id",
                new { id });
            var reservedSlots = tournamentRow is null
                ? 0
                : TournamentInviteSlots.ResolveFromRow(tournamentRow);

            return Results.Ok(new
            {
                stats,
                reserved_slots = reservedSlots,
            });
        }).RequireAuthorization("Authenticated");
    }

    // ── POST /api/invitations/redeem — helpers ────────────────────────────────────

    private sealed record InviteValidationResult(dynamic? Invite, Guid TournamentId, IResult? Error);
    private sealed record TournamentEligibilityResult(
        dynamic? Tournament, bool IsSoloTournament,
        dynamic? TeamMeta, dynamic? RosterMeta, IResult? Error);
    private sealed record TeamRegistrationResult(dynamic? Participant, Guid TeamId, Guid RosterId, IResult? Error);

    private static async Task<InviteValidationResult> ValidateInviteAsync(
        IDbConnection conn, IDbTransaction tx, string code)
    {
        var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT ti.id, ti.tournament_id, ti.email, ti.status, ti.expires_at,
                   t.name AS tournament_name, t.slug AS tournament_slug
            FROM public.tournament_invitations ti
            JOIN public.tournaments t ON t.id = ti.tournament_id
            WHERE ti.code = @code
            FOR UPDATE OF ti
            """,
            new { code }, tx);

        if (invite is null)
            return new(null, Guid.Empty, Results.NotFound(new { error = "Invitation code was not found." }));

        if (!string.Equals((string)invite.status, "sent", StringComparison.OrdinalIgnoreCase))
            return new(null, Guid.Empty, Results.BadRequest(new { error = "Invitation code is not active." }));

        return new(invite, (Guid)invite.tournament_id, null);
    }

    private static bool IsInviteExpired(DateTime? expiresAt) =>
        expiresAt is null || expiresAt <= DateTime.UtcNow;

    private static IResult? ValidateTeamRosterPresence(RedeemInviteRequest req, bool isSolo) =>
        !isSolo && !req.TeamId.HasValue ? Results.BadRequest(new { error = "Select a team to redeem this invitation." }) :
        !isSolo && !req.RosterId.HasValue ? Results.BadRequest(new { error = "Select a roster that matches this tournament." }) :
        null;

    private static async Task<TournamentEligibilityResult> ValidateTournamentStateAsync(
        IDbConnection conn, IDbTransaction tx, Guid tournamentId,
        RedeemInviteRequest req, UserContext userCtx, GameCatalogService gameCatalog)
    {
        static TournamentEligibilityResult Err(IResult r) => new(null, false, null, null, r);

        var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT id, name, slug, status, max_teams, game, team_size, game_mode,
                   registration_deadline, start_date, settings
            FROM public.tournaments
            WHERE id = @tournamentId AND deleted_at IS NULL
            FOR UPDATE
            """,
            new { tournamentId }, tx);
        if (tournament is null) return Err(Results.NotFound(new { error = "Tournament was not found." }));

        var windowError = TournamentTimelineValidator.ValidateRegistrationWindow(
            (string?)tournament.status,
            (DateTimeOffset?)tournament.registration_deadline,
            (DateTimeOffset?)tournament.start_date,
            TournamentTimelineValidator.ParseRegistrationOpensAt(tournament.settings));
        if (windowError is not null) return Err(Results.BadRequest(new { error = windowError }));

        var teamSize = (int?)tournament.team_size ?? 1;
        string participantMode;
        try
        {
            participantMode = await gameCatalog.ResolveParticipantModeAsync(
                (string)tournament.game, tournament.game_mode as string, teamSize, conn, tx);
        }
        catch { participantMode = teamSize > 1 ? "team" : "solo"; }

        var isSolo = participantMode == "solo";

        var presenceError = ValidateTeamRosterPresence(req, isSolo);
        if (presenceError is not null) return Err(presenceError);

        try
        {
            await gameCatalog.ValidateRegistrationAsync(
                conn, tx, tournamentId,
                isSolo ? null : req.TeamId,
                isSolo ? null : req.RosterId,
                userCtx.UserIdGuid, req.RosterLineup);
        }
        catch (GameCatalogValidationException ex) { return Err(Results.BadRequest(new { error = ex.Message })); }

        if (isSolo) return new(tournament, true, null, null, null);

        var teamId = req.TeamId!.Value;
        var teamMeta = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT t.id, t.name AS team_name, t.owner_id
            FROM public.teams t
            WHERE t.id = @teamId
              AND COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'team'
              AND COALESCE(t.is_solo, false) = false
              AND COALESCE(t.tag, '') NOT LIKE 'mock-%'
            """,
            new { teamId }, tx);
        if (teamMeta is null) return Err(Results.BadRequest(new { error = "Team was not found." }));

        var rosterMeta = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT id, name AS roster_name
            FROM public.team_rosters
            WHERE id = @rosterId AND team_id = @teamId
            """,
            new { rosterId = req.RosterId!.Value, teamId }, tx);
        if (rosterMeta is null) return Err(Results.BadRequest(new { error = "Roster was not found for this team." }));

        return new(tournament, false, teamMeta, rosterMeta, null);
    }

    private static async Task<IResult?> ValidateCapacityAndDuplicateAsync(
        IDbConnection conn, IDbTransaction tx, Guid tournamentId,
        UserContext userCtx, int? maxTeams, bool isSolo, Guid? teamId)
    {
        if (maxTeams.HasValue && maxTeams.Value > 0)
        {
            var count = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM public.tournament_participants WHERE tournament_id = @tournamentId AND status NOT IN ('rejected', 'cancelled')",
                new { tournamentId }, tx);
            if (count >= maxTeams.Value)
                return Results.BadRequest(new { error = "Tournament has reached maximum capacity." });
        }

        if (isSolo)
        {
            var already = await conn.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM public.tournament_participants
                    WHERE tournament_id = @tournamentId
                      AND user_id = @userId
                      AND status NOT IN ('cancelled', 'rejected', 'disqualified')
                )
                """,
                new { tournamentId, userId = userCtx.UserIdGuid }, tx);
            if (already) return Results.Conflict(new { error = "You are already registered for this tournament." });
        }
        else
        {
            var already = await conn.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM public.tournament_participants
                    WHERE tournament_id = @tournamentId
                      AND status NOT IN ('cancelled', 'rejected', 'disqualified')
                      AND (user_id = @userId OR team_captain_id = @userId OR team_id = @teamId)
                )
                """,
                new { tournamentId, userId = userCtx.UserIdGuid, teamId }, tx);
            if (already) return Results.Conflict(new { error = "Your team is already registered for this tournament." });
        }

        return null;
    }

    private static async Task<dynamic> RegisterSoloParticipantAsync(
        IDbConnection conn, IDbTransaction tx, Guid tournamentId, UserContext userCtx)
    {
        var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT username FROM profiles WHERE id = @uid",
            new { uid = userCtx.UserIdGuid }, tx);

        var displayName = (string?)profile?.username ?? "Solo Player";
        var teamMembersJson = JsonSerializer.Serialize(new[] { displayName });

        return await conn.QuerySingleAsync<dynamic>(
            """
            INSERT INTO public.tournament_participants
                (tournament_id, user_id, team_id, team_captain_id, team_name,
                 team_members, team_contact_email,
                 status, participant_type, source, entry_fee_amount, entry_fee_paid, payment_status)
            VALUES
                (@tournamentId, @userId, NULL, @userId, @teamName,
                 @teamMembers::jsonb, @email,
                 'approved'::registration_status, 'solo'::registration_type, 'invite',
                 0, TRUE, 'not_required')
            RETURNING id, tournament_id, user_id, team_id, team_captain_id, team_name,
                      roster_id, roster_name, status, participant_type, source, created_at
            """,
            new
            {
                tournamentId,
                userId = userCtx.UserIdGuid,
                teamName = displayName,
                teamMembers = teamMembersJson,
                email = userCtx.Email,
            }, tx);
    }

    private static async Task<TeamRegistrationResult> RegisterTeamParticipantAsync(
        IDbConnection conn, IDbTransaction tx, Guid tournamentId,
        RedeemInviteRequest req, UserContext userCtx, GameCatalogService gameCatalog,
        dynamic teamMeta, dynamic rosterMeta)
    {
        var teamId = (Guid)teamMeta.id;
        var rosterId = (Guid)rosterMeta.id;
        var usesRosterPool = await gameCatalog.TournamentUsesRosterPoolAsync(conn, tx, tournamentId);

        string teamMembersJson, rosterLineupJson;
        if (usesRosterPool && !string.IsNullOrWhiteSpace(req.RosterLineup))
        {
            try
            {
                (teamMembersJson, rosterLineupJson) = await RosterRegistrationHelper.BuildFromSubmittedLineupAsync(
                    conn, rosterId, req.RosterLineup, tx);
            }
            catch (InvalidOperationException ex)
            {
                return new(null, Guid.Empty, Guid.Empty, Results.BadRequest(new { error = ex.Message }));
            }
        }
        else
        {
            (teamMembersJson, rosterLineupJson) = await RosterRegistrationHelper.BuildRegistrationSnapshotAsync(
                conn, rosterId, tx);
        }

        var teamName = string.IsNullOrWhiteSpace((string?)rosterMeta.roster_name)
            ? (string)teamMeta.team_name
            : (string)rosterMeta.roster_name;

        var participant = await conn.QuerySingleAsync<dynamic>(
            """
            INSERT INTO public.tournament_participants
                (tournament_id, user_id, team_id, team_captain_id, team_name,
                 team_members, roster_lineup, team_contact_email, roster_id, roster_name,
                 status, participant_type, source, entry_fee_amount, entry_fee_paid, payment_status)
            VALUES
                (@tournamentId, @userId, @teamId, @userId, @teamName,
                 @teamMembers::jsonb, @rosterLineup::jsonb, @email, @rosterId, @rosterName,
                 'approved'::registration_status, 'team'::registration_type, 'invite',
                 0, TRUE, 'not_required')
            RETURNING id, tournament_id, user_id, team_id, team_captain_id, team_name,
                      roster_id, roster_name, roster_lineup, status, participant_type, source, created_at
            """,
            new
            {
                tournamentId,
                userId = userCtx.UserIdGuid,
                teamId,
                teamName,
                teamMembers = teamMembersJson,
                rosterLineup = rosterLineupJson,
                email = userCtx.Email,
                rosterId,
                rosterName = (string?)rosterMeta.roster_name
            }, tx);

        return new(participant, teamId, rosterId, null);
    }

    // ── Invitation management — helpers ──────────────────────────────────────────

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

    private static string NormalizeInvitationCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim().ToUpperInvariant();
        Span<char> buffer = stackalloc char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
                buffer[length++] = ch;
        }
        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    private static string BuildInviteRedeemUrl(IConfiguration config, string code, string? slug, Guid tournamentId)
    {
        var frontendUrl = (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');
        var tournamentKey = string.IsNullOrWhiteSpace(slug) ? tournamentId.ToString() : slug;
        return $"{frontendUrl}/invitations/redeem?code={Uri.EscapeDataString(code)}&tournament={Uri.EscapeDataString(tournamentKey)}";
    }
}

public sealed record DraftInvitesRequest(List<string>? Emails = null);
public sealed record SendInvitesRequest(Guid[]? InvitationIds = null);
public sealed record RedeemInviteRequest(string Code, Guid? TeamId = null, Guid? RosterId = null, string? RosterLineup = null);
public sealed record ResendInvitesRequest(Guid[]? InvitationIds = null);
public sealed record CsvImportRequest(string? CsvContent = null);

