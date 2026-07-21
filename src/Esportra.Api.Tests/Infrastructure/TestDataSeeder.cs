using Dapper;
using Npgsql;

namespace Esportra.Api.Tests.Infrastructure;

public sealed class TestDataSeeder(string connectionString)
{
    public async Task<Guid> CreateUserAsync(string email = "test@example.com", string username = "testuser")
    {
        var userId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO auth.users (id, email) VALUES (@id, @email) ON CONFLICT DO NOTHING",
            new { id = userId, email });
        await connection.ExecuteAsync(
            "INSERT INTO public.profiles (id, username) VALUES (@id, @username) ON CONFLICT DO NOTHING",
            new { id = userId, username });
        return userId;
    }

    public async Task GrantSuperAdminAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var roleId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO public.admin_roles (key, name) VALUES ('super_admin', 'Super Admin') ON CONFLICT (key) DO UPDATE SET key = EXCLUDED.key RETURNING id");
        await connection.ExecuteAsync(
            "INSERT INTO public.admin_user_roles (user_id, role_id) VALUES (@userId, @roleId) ON CONFLICT DO NOTHING",
            new { userId, roleId });
    }

    public async Task<Guid> CreateSponsorAsync(string name = "Test Sponsor", string tier = "radiant", bool isActive = true)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO public.sponsors (id, name, tier, is_active, logo_url)
            VALUES (@id, @name, @tier, @isActive, 'https://example.com/logo.png')
            """,
            new { id, name, tier, isActive });
        return id;
    }

    public async Task<Guid> CreateTournamentAsync(Guid organizerId, string name = "Test Tournament")
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO public.tournaments (id, name, slug, game, status, format, organizer_id, is_public, max_teams, team_size, start_date)
            VALUES (@id, @name, @slug, 'valorant', 'published', 'single_elimination', @organizerId, true, 8, 5, NOW() + INTERVAL '7 days')
            """,
            new { id, name, slug = $"test-{id:N}"[..30], organizerId });
        return id;
    }

    public async Task<Guid> CreateOrganizationAsync(Guid ownerId, string name = "Test Org")
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO public.organizations (id, name, slug, owner_id)
            VALUES (@id, @name, @slug, @ownerId)
            ON CONFLICT DO NOTHING
            """,
            new { id, name, slug = $"test-org-{id:N}"[..30], ownerId });
        return id;
    }

    public async Task<Guid> CreatePlacementAssetAsync(Guid uploadedBy, string zone = "wide_partner", string role = "banner")
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO public.sponsor_placement_assets
                (id, bucket, object_path, public_url, placement_zone, asset_role, mime_type, width, height, byte_size, uploaded_by, expires_at)
            VALUES (@id, 'system.assets.partners', @path, @url, @zone, @role, 'image/jpeg', 1600, 700, 50000, @uploadedBy, NOW() + INTERVAL '1 hour')
            """,
            new { id, path = $"placements/{zone}/{role}/{id:N}.jpg", url = $"https://example.com/storage/{id:N}.jpg", zone, role, uploadedBy });
        return id;
    }
}
