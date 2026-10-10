using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace Esportra.Api.Endpoints;

internal static class BroadcastHelpers
{
    /// <summary>SHA-256 lowercase hex of the UTF-8 bytes of <paramref name="value"/>.</summary>
    internal static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static async Task<bool> IsOrgOwnerAsync(
        System.Data.IDbConnection conn, Guid orgId, Guid userId,
        System.Data.IDbTransaction? tx = null) =>
        await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId)",
            new { orgId, userId }, tx);
}
