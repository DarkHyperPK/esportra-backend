using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// Generates test JWTs signed with <see cref="TestJwtSecret"/>.
/// The <see cref="ApiFactory"/> configures the app to accept this secret.
/// </summary>
public static class TestAuthHelper
{
    public const string TestJwtSecret = "esportra-test-jwt-secret-that-is-256-bits!!";
    public const string TestAudience = "authenticated";

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Creates a bearer token for a plain authenticated user (no special roles in DB).</summary>
    public static string TokenForUser(Guid userId, string? email = null) =>
        CreateToken(userId, email ?? $"{userId}@test.esportra.com");

    /// <summary>Creates the Authorization header value ready to pass to HttpClient.</summary>
    public static string BearerHeader(Guid userId, string? email = null) =>
        $"Bearer {TokenForUser(userId, email)}";

    private static string CreateToken(Guid userId, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim(JwtRegisteredClaimNames.Aud, TestAudience),
            ]),
            Audience = TestAudience,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return Handler.CreateToken(descriptor);
    }
}
