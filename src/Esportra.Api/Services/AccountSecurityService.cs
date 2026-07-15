using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.Services;

public sealed record AccountSecurityState(
    long SessionsValidAfter,
    long RevocationVersion,
    string AccountStatus);

public sealed class AccountSecurityService(IDbConnectionFactory connectionFactory)
{
    public async Task<AccountSecurityState> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        var state = await connection.QuerySingleOrDefaultAsync<AccountSecurityState>(
            """
            SELECT sessions_valid_after AS SessionsValidAfter,
                   revocation_version AS RevocationVersion,
                   'active' AS AccountStatus
            FROM public.account_security_state
            WHERE user_id = @userId
            """,
            new { userId });

        return state ?? new AccountSecurityState(0, 0, "active");
    }

    public async Task<AccountSecurityState> RevokeAllAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken)
    {
        var revokedAfter = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<AccountSecurityState>(
            """
            INSERT INTO public.account_security_state (
                user_id,
                sessions_valid_after,
                revocation_version,
                updated_at)
            VALUES (@userId, @revokedAfter, 1, NOW())
            ON CONFLICT (user_id) DO UPDATE
            SET sessions_valid_after = EXCLUDED.sessions_valid_after,
                revocation_version = account_security_state.revocation_version + 1,
                updated_at = NOW()
            RETURNING sessions_valid_after AS SessionsValidAfter,
                      revocation_version AS RevocationVersion,
                      'active' AS AccountStatus
            """,
            new { userId, revokedAfter });
    }

    public static bool IsTokenValid(long issuedAt, long sessionsValidAfter)
        => issuedAt > sessionsValidAfter;
}