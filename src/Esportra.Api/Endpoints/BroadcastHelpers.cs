using System.Security.Cryptography;
using System.Text;
using Dapper;
using Esportra.Contracts;

namespace Esportra.Api.Endpoints;

internal sealed record NodeBearerIdentity(Guid NodeId, Guid OrgId);

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

    internal static async Task<NodeBearerIdentity?> ValidateNodeBearerAsync(
        HttpContext ctx, IDbConnectionFactory db)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!authHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authHeader[bearer.Length..].Trim();
        if (string.IsNullOrEmpty(token)) return null;

        var tokenHash = HashString(token);
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<NodeBearerIdentity>(
            """
            SELECT id AS NodeId, org_id AS OrgId
            FROM broadcast_nodes
            WHERE token_hash = @tokenHash AND is_active = true
            """,
            new { tokenHash });
    }
}
