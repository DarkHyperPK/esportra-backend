namespace Esportra.Infrastructure.Supabase;

public interface ISupabaseAdminClient
{
    /// <summary>Delete a user from Supabase Auth.</summary>
    Task DeleteUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Update a user's metadata or email in Supabase Auth.</summary>
    Task UpdateUserAsync(string userId, object updates, CancellationToken ct = default);

    /// <summary>Generate a recovery action link for the given email.</summary>
    Task<GeneratedLink> GenerateRecoveryLinkAsync(string email, string redirectUrl, CancellationToken ct = default);

    /// <summary>Verify a token_hash via GoTrue /auth/v1/verify. Returns the user if valid, null otherwise.</summary>
    Task<SupabaseUser?> VerifyOtpAsync(string tokenHash, string type, CancellationToken ct = default);

    Task<GeneratedLink> GenerateInviteLinkAsync(string email, string redirectUrl, CancellationToken ct = default);

    /// <summary>
    /// Generate a magic link token_hash for an existing user.
    /// Used to establish a session for OAuth-only users during partner onboarding.
    /// </summary>
    Task<GeneratedLink> GenerateMagicLinkAsync(string email, string redirectUrl, CancellationToken ct = default);

    /// <summary>Look up a user by email (admin API).</summary>
    Task<SupabaseUser?> GetUserByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Create a new user in Supabase Auth (email_confirm=true).</summary>
    Task<SupabaseUser> CreateUserAsync(string email, object? userMetadata = null, CancellationToken ct = default);

    /// <summary>
    /// List users from Supabase Auth admin API with pagination.
    /// Returns (users, total) where users is a list of <see cref="SupabaseAuthUser"/>.
    /// </summary>
    Task<SupabaseUserListResult> ListUsersAsync(int page = 1, int perPage = 50, CancellationToken ct = default);

    /// <summary>
    /// Invalidate all refresh tokens for a user, forcing them to re-authenticate.
    /// Uses PUT /auth/v1/admin/users/{userId} to update the user's banned_until,
    /// then immediately unbans them — effectively invalidating all sessions.
    /// </summary>
    Task LogoutUserAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Unlink a specific OAuth identity from a user.
    /// Uses DELETE /auth/v1/admin/users/{userId}/identities/{identityId}.
    /// </summary>
    Task UnlinkIdentityAsync(string userId, string identityId, CancellationToken ct = default);
}

public sealed record GeneratedLink(string TokenHash, string ActionLink, string? UserId);
public sealed record SupabaseUser(string Id, string Email);

/// <summary>Full user record from the GoTrue admin list-users endpoint.</summary>
public sealed record SupabaseAuthUser(
    string Id,
    string Email,
    DateTimeOffset? LastSignInAt,
    DateTimeOffset CreatedAt);

/// <summary>Paginated result from the GoTrue admin list-users endpoint.</summary>
public sealed record SupabaseUserListResult(
    IReadOnlyList<SupabaseAuthUser> Users,
    int Total);

