using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Esportra.Infrastructure.Supabase;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

/// <summary>
/// Suspends a platform account: marks the profile, signs the user out of Supabase, evicts cached
/// context and writes the audit entry. Shared by the admin user page and Squad Up report resolution.
/// </summary>
public sealed class UserSuspensionService(
    IDbConnectionFactory db,
    ISupabaseAdminClient supabase,
    HybridCache cache,
    AuditService audit,
    ILogger<UserSuspensionService> logger)
{
    public async Task SuspendAsync(
        HttpContext ctx,
        UserContext admin,
        Guid userId,
        string reason,
        string? suspensionType,
        DateTime? suspensionUntil,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var targetName = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT COALESCE(full_name, username, id::text) FROM profiles WHERE id = @userId", new { userId });
        await conn.ExecuteAsync(
            """
            UPDATE profiles
            SET is_suspended = true,
                suspension_reason = @reason,
                suspension_type = @suspensionType,
                suspension_until = @suspensionUntil,
                updated_at = NOW()
            WHERE id = @userId
            """,
            new { userId, reason, suspensionType, suspensionUntil });

        try
        {
            await supabase.LogoutUserAsync(userId.ToString(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Supabase force-logout failed for suspended user {UserId}", userId);
        }

        try
        {
            await cache.RemoveAsync($"user-ctx:{userId}", ct);
            await cache.RemoveAsync($"user-suspension:{userId}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache eviction failed for suspended user {UserId}", userId);
        }

        await audit.LogFromHttp(
            ctx, admin,
            ActionType.Suspend, TargetType.User,
            userId, targetName ?? userId.ToString(),
            new { reason, suspensionType, suspensionUntil },
            ct: ct);
    }
}
