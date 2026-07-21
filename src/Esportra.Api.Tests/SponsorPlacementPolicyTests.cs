using Esportra.Api.Auth;
using Esportra.Contracts.Auth;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class SponsorPlacementPolicyTests
{
    [Theory]
    [InlineData("GET", "/api/placements/global")]
    [InlineData("GET", "/api/tournaments/34c1b70a-13fd-4ca2-8106-7e3278cb5d09/sponsors")]
    public void Manifest_ResolvesPublicPlacementReads(string method, string path)
    {
        var rule = RoutePermissionManifest.Resolve(method, path);

        Assert.NotNull(rule);
        Assert.Equal(AuthLevel.Public, rule.Level);
    }

    [Fact]
    public void Manifest_ResolvesSponsorOwnedPlacementsAsAuthenticated()
    {
        var rule = RoutePermissionManifest.Resolve("GET", "/api/sponsors/me/placements");

        Assert.NotNull(rule);
        Assert.Equal(AuthLevel.Authenticated, rule.Level);
    }

    [Theory]
    [InlineData("GET", "/api/admin/placements")]
    [InlineData("POST", "/api/admin/placements")]
    [InlineData("POST", "/api/admin/placement-assets")]
    [InlineData("PUT", "/api/admin/placements/34c1b70a-13fd-4ca2-8106-7e3278cb5d09")]
    [InlineData("PUT", "/api/admin/placements/34c1b70a-13fd-4ca2-8106-7e3278cb5d09/resolve")]
    [InlineData("DELETE", "/api/admin/placements/34c1b70a-13fd-4ca2-8106-7e3278cb5d09")]
    [InlineData("GET", "/api/admin/sponsors/34c1b70a-13fd-4ca2-8106-7e3278cb5d09/placements")]
    [InlineData("GET", "/api/admin/tournaments/34c1b70a-13fd-4ca2-8106-7e3278cb5d09/placements")]
    public void Manifest_RequiresSponsorsEditForPlacementAdministration(string method, string path)
    {
        var rule = RoutePermissionManifest.Resolve(method, path);

        Assert.NotNull(rule);
        Assert.Equal(AuthLevel.AdminPermission, rule.Level);
        Assert.Contains(Permissions.SponsorsEdit, rule.RequiredPermissions ?? []);
    }

    [Fact]
    public void SlotMigration_UsesDeterministicBackfillOrdering()
    {
        var migration = File.ReadAllText(FindMigration());
        var rankingStart = migration.IndexOf("ROW_NUMBER() OVER", StringComparison.Ordinal);
        var rankingEnd = migration.IndexOf(") AS assigned_slot", rankingStart, StringComparison.Ordinal);
        var ranking = migration[rankingStart..rankingEnd];

        Assert.Contains("ORDER BY priority DESC, created_at ASC, id ASC", ranking, StringComparison.Ordinal);
        Assert.DoesNotContain("NOW()", ranking, StringComparison.Ordinal);
    }

    [Fact]
    public void SlotMigration_PreservesDefaultDenyRlsFromPlacementTable()
    {
        var initialMigration = File.ReadAllText(FindMigration("20260718130000_sponsor_placements.sql"));

        Assert.Contains("ENABLE ROW LEVEL SECURITY", initialMigration, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", initialMigration, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL", initialMigration, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetOwnershipMigration_UsesServerOwnedAssetsAndPendingCleanupUniqueness()
    {
        var migration = File.ReadAllText(FindMigration("20260719170000_sponsor_placement_asset_ownership.sql"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS public.sponsor_placement_assets", migration, StringComparison.Ordinal);
        Assert.Contains("banner_asset_id", migration, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at", migration, StringComparison.Ordinal);
    }

    private static string FindMigration(string name = "20260719120000_slot_based_sponsor_placements.sql")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Esportra.Infrastructure", "Migrations", "Scripts", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate migration {name}.");
    }
}
