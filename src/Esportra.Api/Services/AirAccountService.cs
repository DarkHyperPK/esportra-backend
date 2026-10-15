using System.Data;
using Dapper;

namespace Esportra.Api.Services;

public sealed class AirAccountService
{
    public static readonly Guid TrialPlanId = Guid.Parse("06100000-0000-4000-8000-000000000001");
    public async Task EnsureAsync(IDbConnection connection, Guid userId)
    {
        if (connection.State != ConnectionState.Open) connection.Open();
        using var tx = connection.BeginTransaction();
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(CAST(@userId AS text),0))", new { userId }, tx);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_accounts WHERE user_id=@userId)", new { userId }, tx))
        {
            var licenseId = Guid.NewGuid();
            await connection.ExecuteAsync("""
                INSERT INTO licenses(id,user_id,license_id,license_type,status,expires_at,notes)
                VALUES(@licenseId,@userId,@code,'broadcaster','active',now()+interval '14 days','Esportra Air account trial');
                INSERT INTO air_accounts(user_id,license_id,plan_id,valid_until)
                VALUES(@userId,@licenseId,@planId,now()+interval '14 days');
                INSERT INTO air_workspaces(owner_id,name) VALUES(@userId,'My broadcast workspace');
                """, new { userId, licenseId, planId = TrialPlanId, code = "ESP-BC-AIR-" + licenseId.ToString("N").ToUpperInvariant() }, tx);
        }
        tx.Commit();
    }
    public async Task<bool> HasAccessAsync(IDbConnection connection, Guid userId) => await connection.ExecuteScalarAsync<bool>("""
        SELECT EXISTS(SELECT 1 FROM air_accounts a JOIN licenses l ON l.id=a.license_id JOIN air_plans p ON p.id=a.plan_id
          WHERE a.user_id=@userId AND a.status='active' AND l.status='active' AND p.enabled
            AND (a.valid_until IS NULL OR a.valid_until>now()) AND (l.expires_at IS NULL OR l.expires_at>now()))
        """, new { userId });
    public async Task<bool> OwnsWorkspaceAsync(IDbConnection connection, Guid workspaceId, Guid userId) => await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_workspaces WHERE id=@workspaceId AND owner_id=@userId)", new { workspaceId, userId });
}
