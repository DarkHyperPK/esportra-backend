using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Encrypts/decrypts OAuth state parameters using .NET Data Protection.
/// Bundles userId, provider, PKCE verifier, and timestamp into a tamper-proof token.
/// </summary>
public sealed class OAuthStateProtector
{
    private readonly IDataProtector _protector;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public OAuthStateProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Esportra.OAuth.State.v1");
    }

    /// <summary>
    /// Creates an encrypted, tamper-proof state token.
    /// </summary>
    public string Protect(OAuthStatePayload payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return _protector.Protect(json);
    }

    /// <summary>
    /// Decrypts and validates an OAuth state token. Returns null if invalid or expired.
    /// </summary>
    public OAuthStatePayload? Unprotect(string state)
    {
        try
        {
            var json = _protector.Unprotect(state);
            var payload = JsonSerializer.Deserialize<OAuthStatePayload>(json);
            if (payload is null) return null;

            // Reject expired tokens
            if (DateTime.UtcNow - payload.CreatedAt > MaxAge) return null;

            return payload;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a cryptographically secure PKCE code verifier (43–128 chars, URL-safe).
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    /// <summary>
    /// Computes the S256 code challenge from a code verifier.
    /// </summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}

public sealed record OAuthStatePayload(
    string UserId,
    string Provider,
    string? CodeVerifier,
    DateTime CreatedAt);
