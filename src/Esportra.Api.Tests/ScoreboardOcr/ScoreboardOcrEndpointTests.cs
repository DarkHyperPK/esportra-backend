using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.ScoreboardOcr;
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

    private sealed record Seeded(Guid MatchId, Guid Team1Id, Guid Team2Id, Guid Captain1, Guid Captain2);

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
        return new Seeded(matchId, team1, team2, captain1, captain2);
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

    private async Task<int> CountParsesAsync(Guid matchId)
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM scoreboard_ocr_parses WHERE match_id = @matchId", new { matchId });
    }
}
