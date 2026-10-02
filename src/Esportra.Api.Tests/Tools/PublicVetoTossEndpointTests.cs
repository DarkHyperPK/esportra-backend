using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Tools;

/// <summary>
/// Integration tests for the toss system on the public map veto tool (PROJ-044 T2).
/// Covers AC1, AC4–AC6, AC12–AC14, AC22 and the ApplyAction state guards (2f).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PublicVetoTossEndpointTests
{
    private readonly ApiFactory _factory;

    private static readonly string[] SevenMaps = ["m1", "m2", "m3", "m4", "m5", "m6", "m7"];

    public PublicVetoTossEndpointTests(ApiFactory factory)
    {
        _factory = factory;
    }

    // ── AC1: session created in pending_toss ──────────────────────────────────

    [Fact]
    public async Task Create_ReturnsSessionInPendingTossState()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/tools/map-veto", new
        {
            game = "valorant",
            bestOf = 1,
            team1Name = "Alpha",
            team2Name = "Beta",
            mapIds = SevenMaps,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("session").GetProperty("status").GetString().Should().Be("pending_toss");
        body.GetProperty("session").GetProperty("currentTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("session").GetProperty("tossWinnerTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    // ── AC4: toss endpoint is host-token only ─────────────────────────────────

    [Fact]
    public async Task Toss_TeamToken_Returns404()
    {
        var (_, team1Token, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/tools/map-veto/host/{team1Token}/toss", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Toss_InvalidToken_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/tools/map-veto/host/completely-invalid-token/toss", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── AC5: toss 409 when not in pending_toss ────────────────────────────────

    [Fact]
    public async Task Toss_AlreadyTossed_Returns409()
    {
        var (hostToken, _, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/toss", null);

        var response = await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/toss", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Toss has already been resolved for this session.");
    }

    // ── AC6: valid toss produces CSPRNG winner and enters toss_choice_pending ─

    [Fact]
    public async Task Toss_ValidHostToken_TransitionsToTossChoicePending()
    {
        var (hostToken, _, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/toss", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("toss_choice_pending");
        body.GetProperty("tossWinnerTeamId").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("tossWinnerName").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("tossCompletedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null); // F3
        body.GetProperty("currentTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Toss_WinnerIsOneOfTheTwoTeams()
    {
        var (hostToken, _, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/toss", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var winnerName = body.GetProperty("tossWinnerName").GetString();
        winnerName.Should().BeOneOf("Alpha", "Beta");
    }

    // ── AC12: choice endpoint is winning team token only ──────────────────────

    [Fact]
    public async Task TossChoice_LosingTeam_Returns403()
    {
        var (hostToken, team1Token, team2Token) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var loserToken = GetLoserToken(tossBody, team1Token, team2Token);

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{loserToken}/toss-choice",
            new { goFirst = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Only the toss winner can make this choice.");
    }

    [Fact]
    public async Task TossChoice_HostToken_Returns404()
    {
        var (hostToken, _, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        await RunTossAsync(client, hostToken);

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{hostToken}/toss-choice",
            new { goFirst = true });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── AC13: choice 409 guards ───────────────────────────────────────────────

    [Fact]
    public async Task TossChoice_BeforeToss_Returns409WithMessage()
    {
        var (_, team1Token, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{team1Token}/toss-choice",
            new { goFirst = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Toss has not been run yet.");
    }

    [Fact]
    public async Task TossChoice_AfterChoiceAlreadyMade_Returns409WithMessage()
    {
        var (hostToken, team1Token, team2Token) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var winnerToken = GetWinnerToken(tossBody, team1Token, team2Token);
        await client.PostAsJsonAsync($"/api/tools/map-veto/team/{winnerToken}/toss-choice", new { goFirst = true });

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{winnerToken}/toss-choice",
            new { goFirst = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Choice has already been made.");
    }

    // ── AC14: goFirst=true → winner acts first ────────────────────────────────

    [Fact]
    public async Task TossChoice_GoFirst_WinnerActsFirst()
    {
        var (hostToken, team1Token, team2Token) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var winnerId = tossBody.GetProperty("tossWinnerTeamId").GetGuid();
        var winnerToken = GetWinnerToken(tossBody, team1Token, team2Token);

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{winnerToken}/toss-choice",
            new { goFirst = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("in_progress");
        body.GetProperty("tossFirstActorTeamId").GetGuid().Should().Be(winnerId);
        body.GetProperty("currentTeamId").GetGuid().Should().Be(winnerId);
    }

    // ── AC15: goFirst=false → opponent acts first ─────────────────────────────

    [Fact]
    public async Task TossChoice_GiveFirst_OpponentActsFirst()
    {
        var (hostToken, team1Token, team2Token) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var winnerId = tossBody.GetProperty("tossWinnerTeamId").GetGuid();
        var team1Id = tossBody.GetProperty("team1Id").GetGuid();
        var team2Id = tossBody.GetProperty("team2Id").GetGuid();
        var opponentId = winnerId == team1Id ? team2Id : team1Id;
        var winnerToken = GetWinnerToken(tossBody, team1Token, team2Token);

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{winnerToken}/toss-choice",
            new { goFirst = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("in_progress");
        body.GetProperty("tossFirstActorTeamId").GetGuid().Should().Be(opponentId);
        body.GetProperty("currentTeamId").GetGuid().Should().Be(opponentId);
    }

    // ── AC22: reset from any toss state returns to pending_toss ──────────────

    [Fact]
    public async Task Reset_FromTossChoicePending_ReturnsToPendingToss()
    {
        var (hostToken, _, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        await RunTossAsync(client, hostToken);

        var response = await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("pending_toss");
        body.GetProperty("tossWinnerTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("tossFirstActorTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("tossCompletedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null); // F3
    }

    [Fact]
    public async Task Reset_FromInProgress_ClearsTossColumns()
    {
        var (hostToken, team1Token, team2Token) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var winnerToken = GetWinnerToken(tossBody, team1Token, team2Token);
        await client.PostAsJsonAsync($"/api/tools/map-veto/team/{winnerToken}/toss-choice", new { goFirst = true });

        var response = await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("pending_toss");
        body.GetProperty("tossWinnerTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("tossFirstActorTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("currentTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("tossCompletedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null); // F3
    }

    // ── F2: GET polling returns toss fields after toss ────────────────────────

    [Fact]
    public async Task GetHostPolling_AfterToss_ReturnsTossFields()
    {
        var (hostToken, _, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var expectedWinnerId = tossBody.GetProperty("tossWinnerTeamId").GetGuid();

        var response = await client.GetAsync($"/api/tools/map-veto/host/{hostToken}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("toss_choice_pending");
        body.GetProperty("tossWinnerTeamId").GetGuid().Should().Be(expectedWinnerId);
        body.GetProperty("tossWinnerName").GetString().Should().BeOneOf("Alpha", "Beta");
        body.GetProperty("tossCompletedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null); // F3
        body.GetProperty("tossFirstActorTeamId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    // ── F2: GET team polling returns tossFirstActorTeamId after toss-choice ──

    [Fact]
    public async Task GetTeamPolling_AfterTossChoice_ReturnsFirstActorAndInProgress()
    {
        var (hostToken, team1Token, team2Token) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        var tossBody = await RunTossAsync(client, hostToken);
        var winnerId = tossBody.GetProperty("tossWinnerTeamId").GetGuid();
        var winnerToken = GetWinnerToken(tossBody, team1Token, team2Token);
        await client.PostAsJsonAsync($"/api/tools/map-veto/team/{winnerToken}/toss-choice", new { goFirst = true });

        var response = await client.GetAsync($"/api/tools/map-veto/team/{winnerToken}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("in_progress");
        body.GetProperty("tossFirstActorTeamId").GetGuid().Should().Be(winnerId);
        // team role must not receive team1Id / team2Id
        body.GetProperty("team1Id").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        body.GetProperty("team2Id").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    // ── Change 2f: ApplyAction guards for pre-toss states ────────────────────

    [Fact]
    public async Task BanAction_InPendingTossState_Returns409WithMessage()
    {
        var (_, team1Token, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{team1Token}/ban",
            new { mapId = "m1" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Toss has not been run yet.");
    }

    [Fact]
    public async Task BanAction_InTossChoicePendingState_Returns409WithMessage()
    {
        var (hostToken, team1Token, _) = await CreateSessionAsync();
        var client = _factory.CreateClient();
        await RunTossAsync(client, hostToken);

        var response = await client.PostAsJsonAsync(
            $"/api/tools/map-veto/team/{team1Token}/ban",
            new { mapId = "m1" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Toss winner has not made their choice yet.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(string HostToken, string Team1Token, string Team2Token)> CreateSessionAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/tools/map-veto", new
        {
            game = "valorant",
            bestOf = 1,
            team1Name = "Alpha",
            team2Name = "Beta",
            mapIds = SevenMaps,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (
            body.GetProperty("hostToken").GetString()!,
            body.GetProperty("team1Token").GetString()!,
            body.GetProperty("team2Token").GetString()!
        );
    }

    private static async Task<System.Text.Json.JsonElement> RunTossAsync(HttpClient client, string hostToken)
    {
        var response = await client.PostAsync($"/api/tools/map-veto/host/{hostToken}/toss", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    private static string GetWinnerToken(
        System.Text.Json.JsonElement tossBody,
        string team1Token,
        string team2Token)
    {
        var winnerId = tossBody.GetProperty("tossWinnerTeamId").GetGuid();
        var team1Id = tossBody.GetProperty("team1Id").GetGuid();
        return winnerId == team1Id ? team1Token : team2Token;
    }

    private static string GetLoserToken(
        System.Text.Json.JsonElement tossBody,
        string team1Token,
        string team2Token)
    {
        var winnerId = tossBody.GetProperty("tossWinnerTeamId").GetGuid();
        var team1Id = tossBody.GetProperty("team1Id").GetGuid();
        return winnerId == team1Id ? team2Token : team1Token;
    }
}
