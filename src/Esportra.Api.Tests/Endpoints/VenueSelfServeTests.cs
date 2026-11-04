using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>
/// Self-serve venue listing: any signed-in user may list a venue, the first listing grants
/// venue_owner, and an owner can only save a draft or submit for review. Unpublished venues
/// stay visible only to their owner.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class VenueSelfServeTests(ApiFactory factory)
{
    private static object VenueBody(string? status = null) => new
    {
        name = "Test Arena",
        address = "1 Test Street",
        city = "Lahore",
        country = "Pakistan",
        stations = 10,
        slug = $"test-arena-{Guid.NewGuid():N}"[..24],
        pricePerHour = 5m,
        status,
        submittedAt = status is null ? null : DateTime.UtcNow.ToString("O"),
    };

    private async Task<Guid> NewUserAsync()
    {
        var userId = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(userId);
        return userId;
    }

    private static async Task<(Guid Id, string Status)> ReadCreatedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("status").GetString()!);
    }

    [Fact]
    public async Task CreateVenue_Unauthenticated_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/venues", VenueBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateVenue_UserWithoutRole_CreatesDraftAndGrantsVenueOwner()
    {
        var userId = await NewUserAsync();

        var response = await factory.CreateAuthenticatedClient(userId).PostAsJsonAsync("/api/venues", VenueBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var (_, status) = await ReadCreatedAsync(response);
        status.Should().Be("draft");

        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id = @userId AND role = 'venue_owner' AND is_active)",
            new { userId })).Should().BeTrue();
        (await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM verified_roles WHERE user_id = @userId AND role = 'venue_owner' AND status = 'approved' AND is_active)",
            new { userId })).Should().BeTrue();
    }

    [Fact]
    public async Task CreateVenue_SecondListing_KeepsSingleRoleRow()
    {
        var userId = await NewUserAsync();
        var client = factory.CreateAuthenticatedClient(userId);

        (await client.PostAsJsonAsync("/api/venues", VenueBody())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/venues", VenueBody())).StatusCode.Should().Be(HttpStatusCode.Created);

        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM user_roles WHERE user_id = @userId AND role = 'venue_owner'",
            new { userId })).Should().Be(1);
    }

    [Fact]
    public async Task CreateVenue_SubmitForReview_IsSavedAsPendingReview()
    {
        var userId = await NewUserAsync();

        var response = await factory.CreateAuthenticatedClient(userId)
            .PostAsJsonAsync("/api/venues", VenueBody("pending_review"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadCreatedAsync(response)).Status.Should().Be("pending_review");
    }

    [Fact]
    public async Task CreateVenue_OwnerCannotSelfPublish()
    {
        var userId = await NewUserAsync();

        var response = await factory.CreateAuthenticatedClient(userId)
            .PostAsJsonAsync("/api/venues", VenueBody("published"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadCreatedAsync(response)).Status.Should().Be("draft");
    }

    [Fact]
    public async Task ListVenues_ByOwnerId_HidesUnpublishedFromEveryoneButTheOwner()
    {
        var ownerId = await NewUserAsync();
        var otherId = await NewUserAsync();
        var created = await factory.CreateAuthenticatedClient(ownerId).PostAsJsonAsync("/api/venues", VenueBody());
        var (venueId, _) = await ReadCreatedAsync(created);

        async Task<IEnumerable<Guid>> ListAsync(HttpClient client)
        {
            var rows = await client.GetFromJsonAsync<JsonElement[]>($"/api/venues?owner_id={ownerId}");
            return rows!.Select(r => r.GetProperty("id").GetGuid());
        }

        (await ListAsync(factory.CreateAuthenticatedClient(ownerId))).Should().Contain(venueId);
        (await ListAsync(factory.CreateClient())).Should().NotContain(venueId);
        (await ListAsync(factory.CreateAuthenticatedClient(otherId))).Should().NotContain(venueId);
    }

    [Fact]
    public async Task ListVenues_ByOwnerId_StillShowsPublishedVenuesToOthers()
    {
        var ownerId = await NewUserAsync();
        var created = await factory.CreateAuthenticatedClient(ownerId).PostAsJsonAsync("/api/venues", VenueBody());
        var (venueId, _) = await ReadCreatedAsync(created);
        await using (var conn = factory.Seeder.OpenConnection())
            await conn.ExecuteAsync("UPDATE venues SET status = 'published' WHERE id = @venueId", new { venueId });

        var rows = await factory.CreateClient().GetFromJsonAsync<JsonElement[]>($"/api/venues?owner_id={ownerId}");

        rows!.Select(r => r.GetProperty("id").GetGuid()).Should().Contain(venueId);
    }
}
