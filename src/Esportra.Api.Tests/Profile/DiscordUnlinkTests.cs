using System.Net;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Profile;

[Collection(IntegrationCollection.Name)]
public sealed class DiscordUnlinkTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    public DiscordUnlinkTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── DELETE /api/profiles/me/discord ──────────────────────────────────────

    [Fact]
    public async Task DeleteDiscord_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.DeleteAsync("/api/profiles/me/discord");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteDiscord_NoDiscordIdentity_Returns404()
    {
        var userId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(userId);

        var client = _factory.CreateAuthenticatedClient(userId);
        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Discord account not linked");
    }

    [Fact]
    public async Task DeleteDiscord_BlockedByActiveTournamentWithDiscordLinkCount_Returns400()
    {
        var userId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(userId);
        var tournamentId = await _seeder.SeedTournamentAsync(
            userId, status: "open",
            settings: """{"discordLinkCount": 1}""");
        await _seeder.SeedParticipantAsync(userId, tournamentId, "approved");

        var client = _factory.CreateAuthenticatedClient(userId);
        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("active_registration");
    }

    [Fact]
    public async Task DeleteDiscord_BlockedByActiveTournamentWithLegacyFlag_Returns400()
    {
        var userId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(userId);
        var tournamentId = await _seeder.SeedTournamentAsync(
            userId, status: "open",
            settings: """{"requireDiscordLink": true}""");
        await _seeder.SeedParticipantAsync(userId, tournamentId, "approved");

        var client = _factory.CreateAuthenticatedClient(userId);
        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("active_registration");
    }

    [Fact]
    public async Task DeleteDiscord_CompletedTournamentDoesNotBlock_Returns404OrOk()
    {
        // Completed/cancelled tournaments must not block unlinking
        var userId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(userId);
        var tournamentId = await _seeder.SeedTournamentAsync(
            userId, status: "completed",
            settings: """{"discordLinkCount": 1}""");
        await _seeder.SeedParticipantAsync(userId, tournamentId, "approved");

        var client = _factory.CreateAuthenticatedClient(userId);
        var response = await client.DeleteAsync("/api/profiles/me/discord");

        // No blocking tournament → proceeds to identity lookup → 404 (no identity seeded)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDiscord_WithLinkedIdentityAndNoBlocker_Returns200AndDeletesIdentity()
    {
        var userId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(userId);
        await _seeder.SeedDiscordIdentityAsync(userId);

        var client = _factory.CreateAuthenticatedClient(userId);
        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();

        // Verify the identity row was deleted from the database
        await using var conn = _seeder.OpenConnection();
        var identityExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM auth.identities WHERE user_id = @userId AND provider = 'discord')",
            new { userId });
        identityExists.Should().BeFalse();
    }
}
