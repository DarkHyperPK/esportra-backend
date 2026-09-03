using Dapper;
using Npgsql;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// Helpers that seed the minimum required rows for a test to pass auth/FK checks.
/// Every public method is idempotent (ON CONFLICT DO NOTHING).
/// </summary>
public sealed class DbSeeder(string connectionString)
{
    public NpgsqlConnection OpenConnection()
    {
        var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Inserts a row into auth.users so FK constraints are satisfied.</summary>
    public async Task SeedAuthUserAsync(Guid userId, string email = "")
    {
        await using var conn = OpenConnection();
        // CI replay schema defines auth.users with only (id, email, created_at, deleted_at) —
        // a minimal FK-satisfaction stub, not the full Supabase auth.users schema.
        await conn.ExecuteAsync("""
            INSERT INTO auth.users (id, email, created_at)
            VALUES (@id, @email, NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = userId, email = string.IsNullOrEmpty(email) ? $"{userId}@test.esportra.com" : email });

        var resolvedEmail = string.IsNullOrEmpty(email) ? $"{userId}@test.esportra.com" : email;
        try
        {
            await conn.ExecuteAsync("""
                INSERT INTO public.profiles (id, username, full_name, email, created_at, updated_at)
                VALUES (@id, @username, @username, @email, NOW(), NOW())
                ON CONFLICT (id) DO NOTHING
                """,
                new { id = userId, username = $"testuser_{userId:N}"[..20], email = resolvedEmail });
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // profiles table doesn't exist in this schema version — skip
        }
    }

    /// <summary>Grants the user a platform-level role.</summary>
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
                 is_public, slug, start_date, end_date, registration_deadline,
                 created_at, updated_at)
            VALUES
                (@id, @name, 'test-game', @format, @status::tournament_status, @organizerId, @maxTeams,
                 FALSE, @slug, NOW(), NOW() + INTERVAL '7 days', NOW() - INTERVAL '1 day',
                 NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = id.Value, name, format, status, organizerId, maxTeams, slug = $"test-{id.Value:N}"[..20] });
        return id.Value;
    }

    /// <summary>Grants the user an admin panel role linked to ops_admin.</summary>
    public async Task SeedAdminPanelRoleAsync(Guid userId)
    {
        await using var conn = OpenConnection();
        // The seeding migration for admin_roles is pre-baseline so it won't re-run
        // against the CI replay schema — ensure the role exists before linking the user.
        await conn.ExecuteAsync("""
            INSERT INTO public.admin_roles (name, key, description)
            VALUES ('Ops Admin', 'ops_admin', 'Full operations access')
            ON CONFLICT (key) DO NOTHING
            """);
        await conn.ExecuteAsync("""
            INSERT INTO public.admin_user_roles (user_id, role_id)
            SELECT @userId, id FROM public.admin_roles WHERE key = 'ops_admin'
            ON CONFLICT (user_id, role_id) DO NOTHING
            """,
            new { userId });
    }

    /// <summary>Seeds a team row and returns its ID.</summary>
    public async Task<Guid> SeedTeamAsync(Guid ownerId, string name = "Test Team")
    {
        var id = Guid.NewGuid();
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.teams (id, name, tag, game, owner_id, created_at, updated_at)
            VALUES (@id, @name, @tag, 'test-game', @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id, name, tag = name[..Math.Min(name.Length, 4)].ToUpperInvariant(), ownerId });

        await conn.ExecuteAsync("""
            INSERT INTO public.team_members (team_id, user_id, role, joined_at)
            VALUES (@teamId, @userId, 'captain', NOW())
            ON CONFLICT (team_id, user_id) DO NOTHING
            """,
            new { teamId = id, userId = ownerId });

        return id;
    }

    /// <summary>Seeds a tournament_stages row and returns its ID.</summary>
    public async Task<Guid> SeedStageAsync(
        Guid tournamentId,
        string format = "single_elimination",
        string name = "Main Stage",
        int stageOrder = 1,
        Guid? id = null)
    {
        id ??= Guid.NewGuid();
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.tournament_stages
                (id, tournament_id, name, format, stage_order, best_of, advancement_count,
                 status, created_at, updated_at)
            VALUES
                (@id, @tournamentId, @name, @format, @stageOrder, 1, 1,
                 'pending', NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = id.Value, tournamentId, name, format, stageOrder });
        return id.Value;
    }

    /// <summary>Seeds a brkt_versions row and returns its ID.</summary>
    public async Task<Guid> SeedBracketVersionAsync(
        Guid tournamentId,
        Guid stageId,
        string status = "draft",
        Guid? id = null)
    {
        id ??= Guid.NewGuid();
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.brkt_versions
                (id, tournament_id, stage_id, version_number, status, created_at)
            VALUES
                (@id, @tournamentId, @stageId,
                 COALESCE((SELECT MAX(version_number) FROM public.brkt_versions WHERE tournament_id = @tournamentId), 0) + 1,
                 @status, NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = id.Value, tournamentId, stageId, status });
        return id.Value;
    }

    /// <summary>Seeds a brkt_matches row and returns its ID.</summary>
    public async Task<Guid> SeedMatchAsync(
        Guid versionId,
        string bracketType = "winners",
        int roundIndex = 0,
        int matchNumber = 1,
        Guid? team1Id = null,
        Guid? team2Id = null,
        string matchStatus = "pending",
        Guid? id = null)
    {
        id ??= Guid.NewGuid();
        await using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.brkt_matches
                (id, version_id, bracket_type, round_index, match_number,
                 team1_id, team2_id, status, best_of, created_at, updated_at)
            VALUES
                (@id, @versionId, @bracketType, @roundIndex, @matchNumber,
                 @team1Id, @team2Id, @matchStatus, 1, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new
            {
                id = id.Value,
                versionId,
                bracketType,
                roundIndex,
                matchNumber,
                team1Id,
                team2Id,
                matchStatus
            });
        return id.Value;
    }
}
