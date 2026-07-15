using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.IdentityModel.Tokens;

namespace Esportra.Api.Services;

public sealed record GhostModeContext(
    Guid SessionId,
    string Jti,
    Guid AdminId,
    Guid TargetUserId,
    string[] Scopes,
    DateTimeOffset ExpiresAt);

public sealed record GhostModeTokenResult(
    string Token,
    Guid SessionId,
    Guid AdminId,
    Guid TargetUserId,
    string TargetLabel,
    string[] Scopes,
    DateTimeOffset ExpiresAt);

public sealed class GhostModeTokenService(
    IConfiguration configuration,
    IDbConnectionFactory db,
    OperationsAuditService audit)
{
    private static readonly JwtSecurityTokenHandler TokenHandler = new();
    private readonly string _jwtSecret = configuration["Supabase:JwtSecret"]
        ?? throw new InvalidOperationException("Supabase:JwtSecret is required.");
    private readonly string _audience = configuration["Supabase:JwtAudience"] ?? "authenticated";
    private readonly string? _issuer = configuration["Supabase:JwtIssuer"];

    public async Task<GhostModeTokenResult> StartAsync(
        HttpContext http,
        UserContext admin,
        Guid targetUserId,
        string reason,
        string[]? scopes,
        CancellationToken ct)
    {
        if (!admin.IsSuperAdmin ||
            !admin.Permissions.Contains(Permissions.ImpersonationStart, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only SuperAdmins with impersonation:start can enter Ghost Mode.");

        var trimmedReason = reason.Trim();
        if (trimmedReason.Length < 12)
            throw new ArgumentException("A detailed reason with at least 12 characters is required.");
        if (targetUserId == admin.UserIdGuid)
            throw new ArgumentException("Admins cannot impersonate themselves.");

        using var conn = db.CreateConnection();
        var enabled = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value #>> '{}' FROM public.system_config WHERE key = 'impersonation.enabled'",
            cancellationToken: ct));
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ghost Mode is disabled by system configuration.");

        var target = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            """
            SELECT id, email, username, full_name
            FROM public.profiles
            WHERE id = @targetUserId
            """,
            new { targetUserId },
            cancellationToken: ct));
        if (target is null)
            throw new KeyNotFoundException("Target user not found.");

        var safeScopes = (scopes is { Length: > 0 } ? scopes : ["support:read"])
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(15);
        var jti = Guid.NewGuid().ToString("N");
        var sessionId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO public.admin_impersonation_sessions
                (jti, admin_id, target_user_id, reason, scopes, expires_at)
            VALUES
                (@jti, @adminId, @targetUserId, @reason, @scopes, @expiresAt)
            RETURNING id
            """,
            new
            {
                jti,
                adminId = admin.UserIdGuid,
                targetUserId,
                reason = trimmedReason,
                scopes = safeScopes,
                expiresAt
            },
            cancellationToken: ct));

        var token = CreateToken(jti, admin.UserIdGuid, targetUserId, (string?)target.email, safeScopes, now, expiresAt);
        var targetLabel = (string?)target.username ?? (string?)target.full_name ?? targetUserId.ToString();

        await audit.WriteFromHttpAsync(
            http,
            admin,
            "impersonation.start",
            "user",
            targetUserId.ToString(),
            new
            {
                before = new { active = false },
                after = new { active = true, session_id = sessionId, expires_at = expiresAt, scopes = safeScopes },
                reason = trimmedReason,
                pii_redaction = "billing, payments, wallets, gdpr and admin paths are blocked during impersonation"
            },
            "critical",
            ct);

        return new GhostModeTokenResult(token, sessionId, admin.UserIdGuid, targetUserId, targetLabel, safeScopes, expiresAt);
    }

    public async Task<GhostModeContext?> ValidateAsync(HttpContext http, CancellationToken ct)
    {
        var impersonatedBy = http.User.FindFirstValue("impersonated_by");
        var targetUserId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        var jti = http.User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (!Guid.TryParse(impersonatedBy, out var adminId) ||
            !Guid.TryParse(targetUserId, out var targetId) ||
            string.IsNullOrWhiteSpace(jti))
            return null;

        var headerAdminId = http.Request.Headers["X-Impersonated-By"].ToString();
        if (!string.Equals(headerAdminId, adminId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Impersonation header mismatch.");

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            """
            UPDATE public.admin_impersonation_sessions
            SET last_seen_at = now()
            WHERE jti = @jti
              AND admin_id = @adminId
              AND target_user_id = @targetId
              AND revoked_at IS NULL
              AND expires_at > now()
            RETURNING id, jti, admin_id, target_user_id, scopes, expires_at
            """,
            new { jti, adminId, targetId },
            cancellationToken: ct));
        if (row is null)
            throw new UnauthorizedAccessException("Impersonation session is expired or revoked.");

        return new GhostModeContext(
            (Guid)row.id,
            (string)row.jti,
            (Guid)row.admin_id,
            (Guid)row.target_user_id,
            (string[])row.scopes,
            (DateTimeOffset)row.expires_at);
    }

    public async Task EndAsync(HttpContext http, GhostModeContext ghost, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.admin_impersonation_sessions
            SET revoked_at = COALESCE(revoked_at, now())
            WHERE id = @sessionId
            """,
            new { sessionId = ghost.SessionId },
            cancellationToken: ct));
    }

    private string CreateToken(
        string jti,
        Guid adminId,
        Guid targetUserId,
        string? targetEmail,
        string[] scopes,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, targetUserId.ToString()),
            new(ClaimTypes.NameIdentifier, targetUserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new("role", "authenticated"),
            new("impersonated_by", adminId.ToString()),
            new("impersonation_scope", string.Join(' ', scopes)),
        };
        if (!string.IsNullOrWhiteSpace(targetEmail))
            claims.Add(new Claim(ClaimTypes.Email, targetEmail));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Audience = _audience,
            Issuer = string.IsNullOrWhiteSpace(_issuer) ? null : _issuer,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials,
        };

        return TokenHandler.WriteToken(TokenHandler.CreateToken(descriptor));
    }
}
