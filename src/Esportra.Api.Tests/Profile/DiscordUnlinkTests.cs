using System.Net;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Infrastructure.Supabase;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        var fakeAdmin = new FakeSupabaseAdminClient();
        var client = CreateClientWithFakeAdmin(userId, fakeAdmin);

        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Discord account not linked");
        fakeAdmin.UnlinkCalled.Should().BeFalse();
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

        var fakeAdmin = new FakeSupabaseAdminClient();
        var client = CreateClientWithFakeAdmin(userId, fakeAdmin);

        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("active_registration");
        fakeAdmin.UnlinkCalled.Should().BeFalse();
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

        var fakeAdmin = new FakeSupabaseAdminClient();
        var client = CreateClientWithFakeAdmin(userId, fakeAdmin);

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

        var fakeAdmin = new FakeSupabaseAdminClient();
        var client = CreateClientWithFakeAdmin(userId, fakeAdmin);

        var response = await client.DeleteAsync("/api/profiles/me/discord");

        // No blocking tournament → proceeds to identity lookup → 404 (no identity seeded)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDiscord_WithLinkedIdentityAndNoBlocker_Returns200AndCallsUnlink()
    {
        var userId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(userId);
        var identityId = await _seeder.SeedDiscordIdentityAsync(userId);

        var fakeAdmin = new FakeSupabaseAdminClient();
        var client = CreateClientWithFakeAdmin(userId, fakeAdmin);

        var response = await client.DeleteAsync("/api/profiles/me/discord");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        fakeAdmin.UnlinkCalled.Should().BeTrue();
        fakeAdmin.LastUnlinkedIdentityId.Should().Be(identityId.ToString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient CreateClientWithFakeAdmin(Guid userId, FakeSupabaseAdminClient fakeAdmin)
    {
        var factoryWithFake = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISupabaseAdminClient>();
                services.AddSingleton<ISupabaseAdminClient>(fakeAdmin);
            });
        });
        var client = factoryWithFake.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }
}

/// <summary>Test double for ISupabaseAdminClient — records unlink calls without hitting Supabase.</summary>
internal sealed class FakeSupabaseAdminClient : ISupabaseAdminClient
{
    public bool UnlinkCalled { get; private set; }
    public string? LastUnlinkedUserId { get; private set; }
    public string? LastUnlinkedIdentityId { get; private set; }

    public Task UnlinkIdentityAsync(string userId, string identityId, CancellationToken ct = default)
    {
        UnlinkCalled = true;
        LastUnlinkedUserId = userId;
        LastUnlinkedIdentityId = identityId;
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateUserAsync(string userId, object updates, CancellationToken ct = default) => Task.CompletedTask;
    public Task<GeneratedLink> GenerateRecoveryLinkAsync(string email, string redirectUrl, CancellationToken ct = default) =>
        Task.FromResult(new GeneratedLink("", "", null));
    public Task<SupabaseUser?> VerifyOtpAsync(string tokenHash, string type, CancellationToken ct = default) =>
        Task.FromResult<SupabaseUser?>(null);
    public Task<GeneratedLink> GenerateInviteLinkAsync(string email, string redirectUrl, CancellationToken ct = default) =>
        Task.FromResult(new GeneratedLink("", "", null));
    public Task<GeneratedLink> GenerateMagicLinkAsync(string email, string redirectUrl, CancellationToken ct = default) =>
        Task.FromResult(new GeneratedLink("", "", null));
    public Task<SupabaseUser?> GetUserByEmailAsync(string email, CancellationToken ct = default) =>
        Task.FromResult<SupabaseUser?>(null);
    public Task<SupabaseUser> CreateUserAsync(string email, object? userMetadata = null, CancellationToken ct = default) =>
        Task.FromResult(new SupabaseUser(Guid.NewGuid().ToString(), email));
    public Task<SupabaseUserListResult> ListUsersAsync(int page = 1, int perPage = 50, CancellationToken ct = default) =>
        Task.FromResult(new SupabaseUserListResult([], 0));
    public Task LogoutUserAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;
}
