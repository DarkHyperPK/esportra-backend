using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
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

    private sealed class RecordingRiotCheck : IScoreboardRiotCheckTrigger
    {
        public List<Guid> Reports { get; } = [];

        public void Queue(Guid reportId) => Reports.Add(reportId);
    }

    private sealed class FakeMatchLookup(RiotMatchLookupResult result, RiotMatchLookupResult? byId = null) : IRiotMatchLookup
    {
        public List<(IReadOnlyList<string> Puuids, RiotMatchQuery Query)> Calls { get; } = [];
        public List<string> ById { get; } = [];

        public Task<RiotMatchLookupResult> FindCustomGameAsync(IReadOnlyList<string> puuids, RiotMatchQuery query, CancellationToken ct)
        {
            Calls.Add((puuids, query));
            return Task.FromResult(result);
        }

        public Task<RiotMatchLookupResult> GetMatchAsync(string matchId, string puuid, CancellationToken ct)
        {
            ById.Add(matchId);
            return Task.FromResult(byId ?? new RiotMatchLookupResult(RiotLookupStatus.NotFound));
        }

        public Task<string?> PuuidOfAsync(string gameName, string tagLine, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingJobs : IBackgroundJobClient
    {
        public List<IState> States { get; } = [];

        public string Create(Job job, IState state)
        {
            States.Add(state);
            return "job-1";
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private WebApplicationFactory<Program> Configured(StubHandler ocr, StubHandler storage, RecordingRiotCheck? riotCheck = null, IRiotMatchLookup? riot = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ScoreboardOcr:BaseUrl", "http://ocr.test:8095");
            builder.UseSetting("ScoreboardOcr:ServiceToken", "test-token");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<ScoreboardOcrClient>().ConfigurePrimaryHttpMessageHandler(() => ocr);
                services.AddHttpClient(Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => storage);
                services.AddScoped<IRiotAccountLookup>(_ => new NoRiot());
                services.AddScoped<IScoreboardRiotCheckTrigger>(_ => riotCheck ?? new RecordingRiotCheck());
                services.AddScoped<IRiotMatchLookup>(_ => riot ?? new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.NotFound)));
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

    private sealed record SubmittedReport(Seeded Seeded, Guid ReportId, WebApplicationFactory<Program> Factory, RecordingRiotCheck RiotCheck);

    /// <summary>A screenshot report naming AlphaCap (team1 captain, linked) and Ringer (on no roster).</summary>
    private Task<SubmittedReport> SubmitReportWithARingerAsync() => SubmitReportAsync(secondRowLinked: false);

    /// <summary>
    /// A screenshot report of AlphaCap (team1 captain) and a second row: Ringer (on no roster), or
    /// BravoCap (team2 captain, linked) when <paramref name="secondRowLinked"/>.
    /// </summary>
    private async Task<SubmittedReport> SubmitReportAsync(bool secondRowLinked)
    {
        var seeded = await SeedMatchAsync();
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
        {
            await conn.ExecuteAsync(
                "INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@id, @puuid, 'AlphaCap', 'EU1')",
                new { id = seeded.Captain1, puuid = $"p-{seeded.Captain1:N}" });
            if (secondRowLinked)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@id, @puuid, 'BravoCap', 'EU2')",
                    new { id = seeded.Captain2, puuid = $"p-{seeded.Captain2:N}" });
            }
        }
        var second = secondRowLinked ? "BravoCap" : "Ringer";
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
                secondRowLinked
                    ? new
                    {
                        side = "enemy", nameRead = "BravoCap", name = new { value = "BravoCap" },
                        rosterMatch = (object?)new { userId = seeded.Captain2.ToString(), team = "team2", source = "riot_verified", method = "read" },
                    }
                    : new { side = "ally", nameRead = "Ringer", name = new { value = "Ringer" }, rosterMatch = (object?)null },
            },
        });
        var riotCheck = new RecordingRiotCheck();
        var factory = Configured(new StubHandler(HttpStatusCode.OK, ocrResult), new StubHandler(HttpStatusCode.OK, "{}"), riotCheck);
        var client = Authenticated(factory, seeded.Captain1);
        // Unique bytes: the same image on another test's match would be flagged as a reused screenshot.
        var image = Png.Concat(Guid.NewGuid().ToByteArray()).ToArray();
        var parse = await (await client.PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id, image)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var response = await client.PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = seeded.Team1Id.ToString(),
            team1Score = 13,
            team2Score = 1,
            source = "ocr",
            ocrParseId = parse.GetProperty("parseId").GetString(),
            matchData = new
            {
                players = new[]
                {
                    new { gameName = "AlphaCap", isTeam1 = true, isTeam2 = false, kills = 10, deaths = 5, assists = 3 },
                    new { gameName = second, isTeam1 = !secondRowLinked, isTeam2 = secondRowLinked, kills = 7, deaths = 7, assists = 2 },
                },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new SubmittedReport(seeded, Guid.Parse(report.GetProperty("id").GetString()!), factory, riotCheck);
    }

    private static ScoreboardRiotCheckJob RiotCheckJob(SubmittedReport submitted, IRiotMatchLookup lookup, IBackgroundJobClient jobs)
    {
        var services = submitted.Factory.Services.CreateScope().ServiceProvider;
        return new ScoreboardRiotCheckJob(
            services.GetRequiredService<IDbConnectionFactory>(), lookup, jobs,
            services.GetRequiredService<ScoreboardReportVerifier>(), services.GetRequiredService<IHubContext<MatchHub>>(),
            services, NullLogger<ScoreboardRiotCheckJob>.Instance);
    }

    private async Task<OcrVerification> StoredVerificationAsync(Guid reportId)
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        var json = await conn.QuerySingleAsync<string>("SELECT ocr_verification::text FROM match_result_reports WHERE id = @reportId", new { reportId });
        return JsonSerializer.Deserialize<OcrVerification>(json, OcrVerificationJson.Options)!;
    }

    [Fact]
    public async Task SubmittedOcrReport_QueuesTheRiotCheck_AsPending()
    {
        var submitted = await SubmitReportWithARingerAsync();

        submitted.RiotCheck.Reports.Should().Equal(submitted.ReportId);
        (await StoredVerificationAsync(submitted.ReportId)).Riot!.Status.Should().Be(RiotCheckStatus.Pending);
    }

    [Fact]
    public async Task RiotCheck_ConfirmsLinkedPlayers_AndFlagsTheUnregisteredOneAndEditedKda()
    {
        var submitted = await SubmitReportWithARingerAsync();
        var captain = $"p-{submitted.Seeded.Captain1:N}";
        var match = new RiotMatch("riot-ocr-1", "Ascent", "custom", 13, 1, DateTimeOffset.UtcNow,
        [
            new RiotMatchPlayer(captain, "AlphaCap", "EU1", "Blue", 10, 5, 3),
            new RiotMatchPlayer("p-ringer", "Ringer", "XYZ", "Blue", 8, 7, 2),
        ]);
        var lookup = new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.Found, match));

        await RiotCheckJob(submitted, lookup, new RecordingJobs()).RunAsync(submitted.ReportId, 0, CancellationToken.None);

        lookup.Calls.Single().Puuids.Should().Equal(captain);
        lookup.Calls.Single().Query.Should().Match<RiotMatchQuery>(q => q.ScoreA == 13 && q.ScoreB == 1);
        var verification = await StoredVerificationAsync(submitted.ReportId);
        verification.Riot!.Status.Should().Be(RiotCheckStatus.Confirmed);
        verification.Players.Select(p => (p.RiotStatus, p.RiotId)).Should().Equal(
            (RiotStatus.Confirmed, "AlphaCap#EU1"), (RiotStatus.Unregistered, "Ringer#XYZ"));
        verification.Flags.Should().Contain(ScoreboardVerification.FlagStatsMismatch);
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        (await conn.QuerySingleAsync<string>("SELECT riot_match_id FROM match_result_reports WHERE id = @id", new { id = submitted.ReportId }))
            .Should().Be("riot-ocr-1");
        var alerts = (await conn.QueryAsync<string>(
            "SELECT message FROM notifications WHERE user_id = @organizer AND type = 'match_report_flag' ORDER BY created_at",
            new { organizer = submitted.Seeded.Organizer })).ToList();
        alerts.Should().HaveCount(2);
        alerts[1].Should().Contain("Ringer#XYZ played but isn't linked").And.Contain("K/D/A differs from Riot");
        (await conn.QuerySingleAsync<string>("SELECT status FROM match_result_reports WHERE id = @id", new { id = submitted.ReportId }))
            .Should().Be("pending", "a report Riot doesn't fully back still waits for the opponent");
    }

    [Fact]
    public async Task RiotCheck_EveryPlayerAndNumberConfirmed_AcceptsWithoutTheOpponent()
    {
        var submitted = await SubmitReportAsync(secondRowLinked: true);
        var match = RiotMatchSamples.Match("riot-ocr-2", "Ascent", 13, 1, DateTimeOffset.UtcNow,
            new RiotMatchSamples.Player($"p-{submitted.Seeded.Captain1:N}", "AlphaCap", "EU1", "Blue", 10, 5, 3),
            new RiotMatchSamples.Player($"p-{submitted.Seeded.Captain2:N}", "BravoCap", "EU2", "Red", 7, 7, 2));

        await RiotCheckJob(submitted, new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.Found, match)), new RecordingJobs())
            .RunAsync(submitted.ReportId, 0, CancellationToken.None);

        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        (await conn.QuerySingleAsync<(string, string?)>(
            "SELECT status, auto_accepted FROM match_result_reports WHERE id = @id", new { id = submitted.ReportId }))
            .Should().Be(("accepted", "riot_confirmed"));
        (await StoredVerificationAsync(submitted.ReportId)).Riot!.AutoAccepted.Should().BeTrue();
        (await conn.QuerySingleAsync<Guid?>(
            "SELECT winner_id FROM brkt_match_games WHERE match_id = @MatchId AND status = 'completed'", submitted.Seeded))
            .Should().Be(submitted.Seeded.Team1Id);
        (await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*)::int FROM notifications WHERE user_id = ANY(@users) AND type = 'result_accepted' AND data->>'auto_accepted' = 'riot_confirmed'",
            new { users = new[] { submitted.Seeded.Captain1, submitted.Seeded.Captain2 } })).Should().Be(2);
    }

    [Fact]
    public async Task RiotCheck_AMatchIdThatDoesNotFitTheReport_IsIgnoredForASearch()
    {
        var submitted = await SubmitReportAsync(secondRowLinked: true);
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
            await conn.ExecuteAsync("UPDATE match_result_reports SET riot_match_id = 'riot-other-game' WHERE id = @id", new { id = submitted.ReportId });
        var otherGame = RiotMatchSamples.Match("riot-other-game", "Ascent", 13, 9, DateTimeOffset.UtcNow,
            new RiotMatchSamples.Player($"p-{submitted.Seeded.Captain1:N}", "AlphaCap", "EU1", "Blue"));
        var lookup = new FakeMatchLookup(
            new RiotMatchLookupResult(RiotLookupStatus.NotFound), new RiotMatchLookupResult(RiotLookupStatus.Found, otherGame));

        await RiotCheckJob(submitted, lookup, new RecordingJobs()).RunAsync(submitted.ReportId, ScoreboardRiotCheckJob.RetryDelays.Length, CancellationToken.None);

        lookup.ById.Should().Equal("riot-other-game");
        lookup.Calls.Should().ContainSingle("a game with a different score is not this report's game");
        (await StoredVerificationAsync(submitted.ReportId)).Riot!.Status.Should().Be(RiotCheckStatus.NotFound);
    }

    [Fact]
    public async Task ParseScreenshot_FindsTheGameInRiotHistory_ForTheReview()
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
            map = new { value = new { name = "Ascent" }, confidence = 0.99 },
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
            },
        });
        var match = RiotMatchSamples.Match("riot-parse-1", "Ascent", 13, 1, DateTimeOffset.UtcNow,
            new RiotMatchSamples.Player($"p-{seeded.Captain1:N}", "AlphaCap", "EU1", "Blue", 21, 9, 4),
            new RiotMatchSamples.Player("p-someone", "Someone", "XYZ", "Red"));
        var lookup = new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.Found, match));
        var client = Authenticated(Configured(new StubHandler(HttpStatusCode.OK, ocrResult), new StubHandler(HttpStatusCode.OK, "{}"), riot: lookup), seeded.Captain1);

        var response = await client.PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var riot = body.GetProperty("riot");
        riot.GetProperty("status").GetString().Should().Be("found");
        riot.GetProperty("match").GetProperty("team1Rounds").GetInt32().Should().Be(13);
        var alpha = riot.GetProperty("match").GetProperty("players").EnumerateArray().First(p => p.GetProperty("gameName").GetString() == "AlphaCap");
        (alpha.GetProperty("tagLine").GetString(), alpha.GetProperty("kills").GetInt32(), alpha.GetProperty("isTeam1").GetBoolean())
            .Should().Be(("EU1", 21, true));
        var row = body.GetProperty("verification").GetProperty("players")[0];
        (row.GetProperty("riotStatus").GetString(), row.GetProperty("riotId").GetString()).Should().Be(("confirmed", "AlphaCap#EU1"));
        lookup.Calls.Single().Query.Map.Should().Be("Ascent");
    }

    [Fact]
    public async Task RiotCheck_GameNotListedYet_TriesAgainLater_ThenRecordsNotFound()
    {
        var submitted = await SubmitReportWithARingerAsync();
        var lookup = new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.NotFound));
        var jobs = new RecordingJobs();

        await RiotCheckJob(submitted, lookup, jobs).RunAsync(submitted.ReportId, 0, CancellationToken.None);

        jobs.States.Should().ContainSingle().Which.Should().BeOfType<ScheduledState>();
        (await StoredVerificationAsync(submitted.ReportId)).Riot!.Status.Should().Be(RiotCheckStatus.Pending);

        await RiotCheckJob(submitted, lookup, jobs).RunAsync(submitted.ReportId, ScoreboardRiotCheckJob.RetryDelays.Length, CancellationToken.None);

        (await StoredVerificationAsync(submitted.ReportId)).Riot!.Status.Should().Be(RiotCheckStatus.NotFound);
    }

    [Fact]
    public async Task RiotCheck_KeyWithoutMatchAccess_RecordsUnavailable_WithoutRetrying()
    {
        var submitted = await SubmitReportWithARingerAsync();
        var jobs = new RecordingJobs();

        await RiotCheckJob(submitted, new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.Unavailable)), jobs)
            .RunAsync(submitted.ReportId, 0, CancellationToken.None);

        jobs.States.Should().BeEmpty();
        var verification = await StoredVerificationAsync(submitted.ReportId);
        verification.Riot!.Status.Should().Be(RiotCheckStatus.Unavailable);
        verification.Players.Should().OnlyContain(p => p.RiotStatus == null);
    }

    [Fact]
    public async Task ParseScreenshot_TheSameGameInRiotHistory_ComesBackReadyToReport()
    {
        var seeded = await SeedMatchAsync();
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
            await conn.ExecuteAsync(
                "INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@id, @puuid, 'Ally1', 'EU1')",
                new { id = seeded.Captain1, puuid = $"p-{seeded.Captain1:N}" });
        var allies = Enumerable.Range(1, 5).Select(i => $"Ally{i}").ToArray();
        var enemies = Enumerable.Range(1, 5).Select(i => $"Enemy{i}").ToArray();
        var ocrResult = JsonSerializer.Serialize(new
        {
            game = "valorant",
            engineVersion = "rapidocr-test",
            map = new { value = new { name = "Ascent" }, confidence = 0.99 },
            allyScore = new { value = 13, confidence = 0.9 },
            enemyScore = new { value = 7, confidence = 0.9 },
            allyTeam = new { value = "team1", confidence = 1 },
            players = allies.Select(n => new { side = "ally", nameRead = n, name = new { value = n } })
                .Concat(enemies.Select(n => new { side = "enemy", nameRead = n, name = new { value = n } })),
        });
        var match = RiotMatchSamples.Match("riot-same", "Ascent", 7, 13, DateTimeOffset.UtcNow,
            [.. allies.Select((n, i) => new RiotMatchSamples.Player(i == 0 ? $"p-{seeded.Captain1:N}" : $"p-{n}", n, "EU1", "Red")),
             .. enemies.Select(n => new RiotMatchSamples.Player($"p-{n}", n, "NA1", "Blue"))]);
        var client = Authenticated(Configured(new StubHandler(HttpStatusCode.OK, ocrResult), new StubHandler(HttpStatusCode.OK, "{}"),
            riot: new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.Found, match))), seeded.Captain1);

        var body = await (await client.PostAsync($"/api/matches/{seeded.MatchId}/reports/parse-screenshot", Form(seeded.Team1Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        var riot = body.GetProperty("riot");
        riot.GetProperty("confident").GetBoolean().Should().BeTrue();
        var candidate = riot.GetProperty("candidate");
        (candidate.GetProperty("id").GetString(), candidate.GetProperty("reporterSide").GetString()).Should().Be(("riot-same", "Red"));
        (candidate.GetProperty("myTeamScore").GetInt32(), candidate.GetProperty("result").GetString()).Should().Be((13, "Victory"));
        candidate.GetProperty("players").GetArrayLength().Should().Be(10);
    }

    [Fact]
    public async Task RiotSourcedReport_EveryAccountLinked_IsAcceptedWithoutTheOpponent()
    {
        var seeded = await SeedMatchAsync();
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@a, @pa, 'AlphaCap', 'EU1');
                INSERT INTO riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@b, @pb, 'BravoCap', 'EU2');
                """,
                new { a = seeded.Captain1, pa = $"p-{seeded.Captain1:N}", b = seeded.Captain2, pb = $"p-{seeded.Captain2:N}" });
        }
        var riotCheck = new RecordingRiotCheck();
        var factory = Configured(new StubHandler(HttpStatusCode.OK, OcrResult), new StubHandler(HttpStatusCode.OK, "{}"), riotCheck);
        var response = await Authenticated(factory, seeded.Captain1).PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = seeded.Team1Id.ToString(),
            team1Score = 13,
            team2Score = 1,
            riotMatchId = "riot-auto-fetch",
            mapName = "Ascent",
            matchData = new
            {
                players = new object[]
                {
                    new { gameName = "AlphaCap", isTeam1 = true, isTeam2 = false, kills = 10, deaths = 5, assists = 3 },
                    new { gameName = "BravoCap", isTeam1 = false, isTeam2 = true, kills = 7, deaths = 7, assists = 2 },
                },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var reportId = Guid.Parse((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        riotCheck.Reports.Should().Equal(reportId);
        var match = RiotMatchSamples.Match("riot-auto-fetch", "Ascent", 13, 1, DateTimeOffset.UtcNow,
            new RiotMatchSamples.Player($"p-{seeded.Captain1:N}", "AlphaCap", "EU1", "Blue", 10, 5, 3),
            new RiotMatchSamples.Player($"p-{seeded.Captain2:N}", "BravoCap", "EU2", "Red", 7, 7, 2));
        var lookup = new FakeMatchLookup(new RiotMatchLookupResult(RiotLookupStatus.NotFound), new RiotMatchLookupResult(RiotLookupStatus.Found, match));

        await RiotCheckJob(new SubmittedReport(seeded, reportId, factory, riotCheck), lookup, new RecordingJobs())
            .RunAsync(reportId, 0, CancellationToken.None);

        lookup.ById.Should().Equal("riot-auto-fetch");
        await using var check = new NpgsqlConnection(_factory.ConnectionString);
        (await check.QuerySingleAsync<(string, string?)>("SELECT status, auto_accepted FROM match_result_reports WHERE id = @reportId", new { reportId }))
            .Should().Be(("accepted", "riot_confirmed"));
        var stored = await StoredVerificationAsync(reportId);
        stored.Players.Select(p => (p.Name, p.RiotStatus)).Should().Equal(("AlphaCap", RiotStatus.Confirmed), ("BravoCap", RiotStatus.Confirmed));
    }

    private async Task<int> CountParsesAsync(Guid matchId)
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM scoreboard_ocr_parses WHERE match_id = @matchId", new { matchId });
    }
}
