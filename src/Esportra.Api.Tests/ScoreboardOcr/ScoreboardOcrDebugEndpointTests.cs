using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Endpoints;
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
public sealed class ScoreboardOcrDebugEndpointTests
{
    private const string OcrResult = """{"game":"valorant","engineVersion":"rapidocr-test","players":[],"allyScore":{"value":13,"confidence":0.9}}""";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly ApiFactory _factory;

    public ScoreboardOcrDebugEndpointTests(ApiFactory factory) => _factory = factory;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
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

    private static HttpClient As(WebApplicationFactory<Program> factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }

    private async Task<Guid> SeedUserAsync(bool admin)
    {
        var id = Guid.NewGuid();
        await _factory.Seeder.SeedAuthUserAsync(id);
        if (admin) await _factory.Seeder.SeedAdminPanelRoleAsync(id);
        return id;
    }

    private static MultipartFormDataContent Form(string? team1 = null, string? team2 = null)
    {
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent { { file, "file", "shot.png" } };
        if (team1 is not null) form.Add(new StringContent(team1), "team1Names");
        if (team2 is not null) form.Add(new StringContent(team2), "team2Names");
        return form;
    }

    [Fact]
    public async Task Parse_Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient().PostAsync("/api/debug/scoreboard-ocr", Form());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Parse_NonStaff_Returns403_WithoutCallingOcr()
    {
        var ocr = new StubHandler(HttpStatusCode.OK, OcrResult);
        var response = await As(Configured(ocr, new StubHandler(HttpStatusCode.OK, "{}")), await SeedUserAsync(admin: false))
            .PostAsync("/api/debug/scoreboard-ocr", Form());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ocr.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Parse_NotConfigured_Returns503()
    {
        var response = await _factory.CreateAuthenticatedClient(await SeedUserAsync(admin: true))
            .PostAsync("/api/debug/scoreboard-ocr", Form());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Parse_Admin_ReturnsResult_SendsRosters_AndStoresNothing()
    {
        var ocr = new StubHandler(HttpStatusCode.OK, OcrResult);
        var storage = new StubHandler(HttpStatusCode.OK, "{}");
        var before = await CountParsesAsync();

        var response = await As(Configured(ocr, storage), await SeedUserAsync(admin: true))
            .PostAsync("/api/debug/scoreboard-ocr", Form("tr1ck\nKenshiro\n", "AHK"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("result").GetProperty("allyScore").GetProperty("value").GetInt32().Should().Be(13);
        body.GetProperty("elapsedMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        ocr.Calls.Should().ContainSingle().Which.Body.Should().Contain("tr1ck").And.Contain("Kenshiro").And.Contain("team2-0");
        storage.Calls.Should().BeEmpty();
        (await CountParsesAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Health_Admin_ReportsReachableAndAgents()
    {
        var ocr = new StubHandler(HttpStatusCode.OK, """{"status":"ok","agents":28}""");

        var response = await As(Configured(ocr, new StubHandler(HttpStatusCode.OK, "{}")), await SeedUserAsync(admin: true))
            .GetAsync("/api/debug/scoreboard-ocr/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeTrue();
        body.GetProperty("reachable").GetBoolean().Should().BeTrue();
        body.GetProperty("agents").GetInt32().Should().Be(28);
    }

    [Fact]
    public void ParseNames_TrimsDedupesAndCaps()
    {
        var names = ScoreboardOcrDebugEndpoints.ParseNames(" a \nA\nb\n\n" + new string('x', 41), "team1");
        names.Select(n => n.Names[0]).Should().Equal("a", "b");
        names.Select(n => n.UserId).Should().Equal("team1-0", "team1-1");
    }

    private async Task<int> CountParsesAsync()
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM scoreboard_ocr_parses");
    }
}
