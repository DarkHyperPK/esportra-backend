using Dapper;
using Npgsql;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// Helpers that seed the minimum required rows for a test to pass auth/FK checks.
/// Every public method is idempotent (ON CONFLICT DO NOTHING / IF NOT EXISTS).
/// </summary>
public sealed class DbSeeder(string connectionString)
{
    public NpgsqlConnection OpenConnection()
    {
        var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Inserts a row into auth.users so FK constraints on user_roles etc. are satisfied.
    /// Also inserts a minimal public.profiles row if the schema requires one.
    /// </summary>
    public async Task SeedAuthUserAsync(Guid userId, string email = "")
    {
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO auth.users (id, email, created_at, updated_at, confirmation_sent_at,
                                    recovery_sent_at, email_change_sent_at, last_sign_in_at,
                                    raw_app_meta_data, raw_user_meta_data,
                                    is_super_admin, role, aud, encrypted_password)
            VALUES (@id, @email, NOW(), NOW(), NULL, NULL, NULL, NULL,
                    '{}'::jsonb, '{}'::jsonb, FALSE, 'authenticated', 'authenticated', '')
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = userId, email = string.IsNullOrEmpty(email) ? $"{userId}@test.esportra.com" : email });

        // Profiles table may have a FK → auth.users; insert minimally if it exists
        try
        {
            await conn.ExecuteAsync("""
                INSERT INTO public.profiles (id, username, display_name, avatar_url, bio, country_code,
                                             created_at, updated_at, is_banned)
                VALUES (@id, @username, @username, NULL, NULL, NULL, NOW(), NOW(), FALSE)
                ON CONFLICT (id) DO NOTHING
                """,
                new { id = userId, username = $"testuser_{userId:N}"[..20] });
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // relation does not exist
        {
            // profiles table doesn't exist in this schema version — skip
        }
    }

    /// <summary>Grants the user a platform-level role (e.g. 'organizer', 'player').</summary>
    public async Task SeedUserRoleAsync(Guid userId, string role)
    {
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.user_roles (user_id, role, is_active, assigned_at)
            VALUES (@userId, @role, TRUE, NOW())
            ON CONFLICT (user_id, role) DO NOTHING
            """,
            new { userId, role });
    }

    /// <summary>Seeds a tournament row and returns its ID.</summary>
    public async Task<Guid> SeedTournamentAsync(
        Guid organizerId,
        string name = "Test Tournament",
        string format = "single_elimination",
        string status = "draft",
        int maxTeams = 8,
        Guid? id = null)
    {
        id ??= Guid.NewGuid();
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.tournaments
                (id, name, game, format, status, organizer_id, max_teams,
                 is_public, slug, created_at, updated_at)
            VALUES
                (@id, @name, 'test-game', @format, @status, @organizerId, @maxTeams,
                 FALSE, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = id.Value, name, format, status, organizerId, maxTeams, slug = $"test-{id.Value:N}"[..20] });
        return id.Value;
    }

    /// <summary>Seeds a team row and returns its ID.</summary>
    public async Task<Guid> SeedTeamAsync(Guid captainId, string name = "Test Team")
    {
        var id = Guid.NewGuid();
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.teams (id, name, captain_id, created_at, updated_at)
            VALUES (@id, @name, @captainId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id, name, captainId });

        await conn.ExecuteAsync("""
            INSERT INTO public.team_members (team_id, user_id, role, is_active, joined_at)
            VALUES (@teamId, @userId, 'captain', TRUE, NOW())
            ON CONFLICT (team_id, user_id) DO NOTHING
            """,
            new { teamId = id, userId = captainId });

        return id;
    }
}
