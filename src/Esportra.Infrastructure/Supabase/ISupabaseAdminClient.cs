namespace Esportra.Infrastructure.Supabase;

public interface ISupabaseAdminClient
{
    /// <summary>Delete a user from Supabase Auth.</summary>
    Task DeleteUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Update a user's metadata or email in Supabase Auth.</summary>
    Task UpdateUserAsync(string userId, object updates, CancellationToken ct = default);

    /// <summary>Set a user's password directly (bypasses email flow).</summary>
    Task SetPasswordAsync(string userId, string password, CancellationToken ct = default);

    /// <summary>
    /// Generate a recovery link token_hash for the given email.
    /// Returns the token_hash (not the full action_link — keeps users on our domain).
    /// </summary>
    Task<GeneratedLink> GenerateRecoveryLinkAsync(string email, CancellationToken ct = default);

    /// <summary>Verify an OTP token_hash and return the user it belongs to.</summary>
    Task<SupabaseUser?> VerifyOtpAsync(string tokenHash, string type, CancellationToken ct = default);

    /// <summary>Look up a user by email (admin API).</summary>
    Task<SupabaseUser?> GetUserByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Create a new user in Supabase Auth (email_confirm=true).</summary>
    Task<SupabaseUser> CreateUserAsync(string email, object? userMetadata = null, CancellationToken ct = default);
}

public sealed record GeneratedLink(string TokenHash, string ActionLink);
public sealed record SupabaseUser(string Id, string Email);
