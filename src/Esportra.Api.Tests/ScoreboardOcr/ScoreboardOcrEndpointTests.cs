using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

[Collection(IntegrationCollection.Name)]
public sealed class ScoreboardOcrEndpointTests
{
    private const string OcrResult = """
        {"game":"valorant","engineVersion":"rapidocr-test","map":{"value":{"name":"Ascent"},"confidence":0.99},
         "allyScore":{"value":13,"confidence":0.9},"enemyScore":{"value":1,"confidence":0.9},"players":[]}
        """;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02];

    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    public ScoreboardOcrEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoRiot : IRiotAccountLookup
    {
        public Task<RiotAccountLookupResult> GetByPuuidAsync(string puuid, CancellationToken ct) =>
            Task.FromResult(new RiotAccountLookupResult(404, null, null));
    }

    private sealed record Seeded(Guid MatchId, Guid Team1Id, Guid Team2Id, Guid Captain1, Guid Captain2, Guid Organizer);

    private async Task<Seeded> SeedMatchAsync(string game = "Valorant")
    {
        var organizer = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(organizer);
        await _seeder.SeedUserRoleAsync(organizer, "organizer");
        var tournamentId = await _seeder.SeedTournamentAsync(organizer);
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
            await conn.ExecuteAsync("UPDATE tournaments SET game = @game WHERE id = @tournamentId", new { game, tournamentId });

        var captain1 = Guid.NewGuid();
        var captain2 = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(captain1);
        await _seeder.SeedAuthUserAsync(captain2);
        var team1 = await _seeder.SeedTeamAsync(captain1, "Alpha");
        var team2 = await _seeder.SeedTeamAsync(captain2, "Bravo");
        var stageId = await _seeder.SeedStageAsync(tournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId, "active");
        var matchId = await _seeder.SeedMatchAsync(versionId, team1Id: team1, team2Id: team2, matchStatus: "in_progress");
        return new Seeded(matchId, team1, team2, captain1, captain2, organizer);
    }

    private WebApplicationFactory<Program> Configured(StubHandler ocr, StubHandler storage) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ScoreboardOcr:BaseUrl", "http://ocr.test:8095");
            builder.UseSetting("ScoreboardOcr:ServiceToken", "test-token");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<ScoreboardOcrClient>().ConfigurePrimaryHttpMessageHandler(() => ocr);
                services.AddHttpClient(Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => storage);
                services.AddScoped<IRiotAccountLookup>(_ => new NoRiot());
            });
        });

    private static HttpClient Authenticated(WebApplicationFactory<Program> factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }

    private static MultipartFormDataContent Form(Guid competitorId, byte[]? image = null, string fileName = "shot.png")
    {
        var file = new ByteArrayContent(image ?? Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent
        {
            { file, "file", fileName },
            { new StringContent(competitorId.ToString()), "reportedByTeamId" },
            { new StringContent("1"), "gameNumber" },
        };
    }

    [Fact]
    public async Task ParseScreenshot_Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient()
            .PostAsync($"/api/matches/{Guid.NewGuid()}/reports/parse-screenshot", Form(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ParseScreenshot_ServiceNotConfigured_Returns503()
    {
        var seeded = await SeedMatchAsync();

        var response = await _factory.CreateAuthenticatedClient(seeded.Captain1)
            .PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ParseScreenshot_NonCaptain_Returns403_WithoutCallingOcr()
    {
        var seeded = await SeedMatchAsync();
        var outsider = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(outsider);
        var ocr = new StubHandler(HttpStatusCode.OK, OcrResult);

        var response = await Authenticated(Configured(ocr, new StubHandler(HttpStatusCode.OK, "{}")), outsider)
            .PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ocr.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseScreenshot_NonValorantTournament_Returns400()
    {
        var seeded = await SeedMatchAsync(game: "Rainbow Six Siege");
        var ocr = new StubHandler(HttpStatusCode.OK, OcrResult);

        var response = await Authenticated(Configured(ocr, new StubHandler(HttpStatusCode.OK, "{}")), seeded.Captain1)
            .PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ocr.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseScreenshot_SpoofedImage_Returns400()
    {
        var seeded = await SeedMatchAsync();
        var ocr = new StubHandler(HttpStatusCode.OK, OcrResult);

        var response = await Authenticated(Configured(ocr, new StubHandler(HttpStatusCode.OK, "{}")), seeded.Captain1)
            .PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id, Encoding.UTF8.GetBytes("<svg/>")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ocr.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseScreenshot_Unreadable_Returns422_AndStoresNothing()
    {
        var seeded = await SeedMatchAsync();
        var storage = new StubHandler(HttpStatusCode.OK, "{}");
        var ocr = new StubHandler(HttpStatusCode.UnprocessableEntity, """{"detail":"Could not find the scoreboard columns."}""");

        var response = await Authenticated(Configured(ocr, storage), seeded.Captain1)
            .PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        storage.Requests.Should().BeEmpty();
        (await CountParsesAsync(seeded.MatchId)).Should().Be(0);
    }

    [Fact]
    public async Task ParseScreenshot_ThenSubmitOcrReport_LinksParseAndEvidence()
    {
        var seeded = await SeedMatchAsync();
        var storage = new StubHandler(HttpStatusCode.OK, "{}");
        var factory = Configured(new StubHandler(HttpStatusCode.OK, OcrResult), storage);
        var client = Authenticated(factory, seeded.Captain1);

        var parseResponse = await client.PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));

        parseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var parse = await parseResponse.Content.ReadFromJsonAsync<JsonElement>();
        var parseId = parse.GetProperty("parseId").GetString();
        var screenshotUrl = parse.GetProperty("screenshotUrl").GetString();
        screenshotUrl.Should().Contain($"tournaments.results/matches/{seeded.MatchId}/ocr/{parseId}.png");
        parse.GetProperty("result").GetProperty("allyScore").GetProperty("value").GetInt32().Should().Be(13);
        storage.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post);
        (await CountParsesAsync(seeded.MatchId)).Should().Be(1);

        var submit = await client.PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = seeded.Team1Id.ToString(),
            team1Score = 13,
            team2Score = 1,
            source = "ocr",
            ocrParseId = parseId,
            screenshotUrls = Array.Empty<string>(),
        });

        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await submit.Content.ReadFromJsonAsync<JsonElement>();
        report.GetProperty("source").GetString().Should().Be("ocr");
        report.GetProperty("ocr_parse_id").GetString().Should().Be(parseId);
        report.GetProperty("screenshot_urls").EnumerateArray().Select(e => e.GetString()).Should().Contain(screenshotUrl);
    }

    [Fact]
    public async Task SubmitOcrReport_WithOpponentsParse_Returns400()
    {
        var seeded = await SeedMatchAsync();
        var factory = Configured(new StubHandler(HttpStatusCode.OK, OcrResult), new StubHandler(HttpStatusCode.OK, "{}"));
        var parseResponse = await Authenticated(factory, seeded.Captain1)
            .PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));
        var parseId = (await parseResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("parseId").GetString();

        var submit = await Authenticated(factory, seeded.Captain2).PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = seeded.Team2Id.ToString(),
            team1Score = 1,
            team2Score = 13,
            source = "ocr",
            ocrParseId = parseId,
        });

        submit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OcrReport_WithAPlayerOffTheRosters_IsVerifiedAndOrganizerAlertedOnce()
    {
        var seeded = await SeedMatchAsync();
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
            await conn.ExecuteAsync(
                "INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@id, @puuid, 'AlphaCap', 'EU1')",
                new { id = seeded.Captain1, puuid = $"p-{seeded.Captain1:N}" });
        var ocrResult = JsonSerializer.Serialize(new
        {
            game = "valorant",
            engineVersion = "rapidocr-test",
            allyScore = new { value = 13, confidence = 0.9 },
            enemyScore = new { value = 1, confidence = 0.9 },
            allyTeam = new { value = "team1", confidence = 1 },
            players = new object[]
            {
                new
                {
                    side = "ally", nameRead = "AlphaCap", name = new { value = "AlphaCap" },
                    rosterMatch = new { userId = seeded.Captain1.ToString(), team = "team1", source = "riot_verified", method = "read" },
                },
                new { side = "ally", nameRead = "Ringer", name = new { value = "Ringer" } },
            },
        });
        var client = Authenticated(Configured(new StubHandler(HttpStatusCode.OK, ocrResult), new StubHandler(HttpStatusCode.OK, "{}")), seeded.Captain1);

        var parse = await (await client.PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id)))
            .Content.ReadFromJsonAsync<JsonElement>();
        parse.GetProperty("verification").GetProperty("players").EnumerateArray().Select(p => p.GetProperty("status").GetString())
            .Should().Equal("verified", "unknown");

        var body = new
        {
            gameNumber = 1,
            reportedByTeamId = seeded.Team1Id.ToString(),
            team1Score = 13,
            team2Score = 1,
            source = "ocr",
            ocrParseId = parse.GetProperty("parseId").GetString(),
            matchData = new { players = new[] { new { gameName = "AlphaCap", isTeam1 = true }, new { gameName = "Ringer", isTeam1 = true } } },
        };
        var first = await client.PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", body);
        await client.PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", body);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await first.Content.ReadFromJsonAsync<JsonElement>();
        report.GetProperty("ocr_verification").GetProperty("players")[1].GetProperty("status").GetString().Should().Be("unknown");
        await using var check = new NpgsqlConnection(_factory.ConnectionString);
        var alerts = await check.QueryAsync<string>(
            "SELECT message FROM notifications WHERE user_id = @organizer AND type = 'match_report_flag'", new { organizer = seeded.Organizer });
        alerts.Should().ContainSingle().Which.Should().Contain("Ringer isn't on either team's roster");
    }

    [Fact]
    public async Task SameScreenshotForAnotherMatch_IsFlaggedAsReused()
    {
        var first = await SeedMatchAsync();
        var second = await SeedMatchAsync();
        byte[] image = [.. Png, .. Guid.NewGuid().ToByteArray()];
        var factory = Configured(new StubHandler(HttpStatusCode.OK, OcrResult), new StubHandler(HttpStatusCode.OK, "{}"));
        await Authenticated(factory, first.Captain1).PostAsync($"/api/matches/{first.MatchId}/reports/parse-screenshot", Form(first.Team1Id, image));

        var parse = await (await Authenticated(factory, second.Captain1)
                .PostAsync($"/api/matches/{second.MatchId}/reports/parse-screenshot", Form(second.Team1Id, image)))
            .Content.ReadFromJsonAsync<JsonElement>();

        parse.GetProperty("verification").GetProperty("flags").EnumerateArray().Select(f => f.GetString())
            .Should().Contain(ScoreboardReportVerifier.FlagReused);
    }

    [Fact]
    public async Task OcrReport_WithEveryoneVerified_DoesNotAlertOrganizers()
    {
        var seeded = await SeedMatchAsync();
        var ocrResult = JsonSerializer.Serialize(new
        {
            allyTeam = new { value = "team1" },
            players = new object[]
            {
                new
                {
                    side = "ally", nameRead = "Cap", name = new { value = "Cap" },
                    rosterMatch = new { userId = seeded.Captain1.ToString(), team = "team1", source = "riot_verified", method = "read" },
                },
            },
        });
        var client = Authenticated(Configured(new StubHandler(HttpStatusCode.OK, ocrResult), new StubHandler(HttpStatusCode.OK, "{}")), seeded.Captain1);
        // A screenshot nobody uploaded before (the shared test PNG would rightly be flagged as reused).
        byte[] unique = [.. Png, .. Guid.NewGuid().ToByteArray()];
        var parse = await (await client.PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id, unique)))
            .Content.ReadFromJsonAsync<JsonElement>();

        var submit = await client.PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = seeded.Team1Id.ToString(),
            team1Score = 13,
            team2Score = 1,
            source = "ocr",
            ocrParseId = parse.GetProperty("parseId").GetString(),
            matchData = new { players = new[] { new { gameName = "Cap", isTeam1 = true } } },
        });

        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var check = new NpgsqlConnection(_factory.ConnectionString);
        (await check.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM notifications WHERE user_id = @organizer AND type = 'match_report_flag'", new { organizer = seeded.Organizer }))
            .Should().Be(0);
    }

    private async Task<int> CountParsesAsync(Guid matchId)
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM scoreboard_ocr_parses WHERE match_id = @matchId", new { matchId });
    }
}
