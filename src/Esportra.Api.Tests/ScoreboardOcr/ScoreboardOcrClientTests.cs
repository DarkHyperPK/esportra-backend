using System.Net;
using System.Text;
using Esportra.Api.ScoreboardOcr;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

public sealed class ScoreboardOcrClientTests
{
    private static readonly OcrRosters NoRosters = new([], []);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request);
        }
    }

    private static (ScoreboardOcrClient Client, StubHandler Handler) Create(HttpStatusCode status, string body, int timeoutSeconds = 30)
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
        return (Build(handler, timeoutSeconds), handler);
    }

    private static ScoreboardOcrClient Build(StubHandler handler, int timeoutSeconds = 30) =>
        new(new HttpClient(handler), Options.Create(new ScoreboardOcrOptions
        {
            BaseUrl = "http://ocr.internal:8095",
            ServiceToken = "secret-token",
            TimeoutSeconds = timeoutSeconds,
        }), NullLogger<ScoreboardOcrClient>.Instance);

    [Fact]
    public async Task ParseValorantAsync_ReturnsResult_AndSendsTokenImageAndRosters()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """{"players":[],"engineVersion":"x"}""");
        var rosters = new OcrRosters([new OcrRosterPlayer("u1", ["Kooltkk"])], []);

        var result = await client.ParseValorantAsync([1, 2, 3], "screenshot.png", "image/png", rosters, CancellationToken.None);

        result.Status.Should().Be(OcrCallStatus.Success);
        result.ResultJson.Should().Contain("players");
        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://ocr.internal:8095/v1/valorant/scoreboard");
        handler.LastRequest.Headers.GetValues("X-Service-Token").Should().ContainSingle("secret-token");
        handler.LastBody.Should().Contain("name=image").And.Contain("\"userId\":\"u1\"").And.Contain("Kooltkk");
    }

    [Fact]
    public async Task ParseValorantAsync_ReturnsUnreadable_WithServiceDetail_On422()
    {
        var (client, _) = Create(HttpStatusCode.UnprocessableEntity, """{"detail":"Could not find the scoreboard columns."}""");

        var result = await client.ParseValorantAsync([1], "s.png", "image/png", NoRosters, CancellationToken.None);

        result.Status.Should().Be(OcrCallStatus.Unreadable);
        result.UserMessage.Should().Be("Could not find the scoreboard columns.");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"detail":"boom"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail":"unauthorized"}""")]
    [InlineData(HttpStatusCode.OK, """{"not":"a scoreboard"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task ParseValorantAsync_ReturnsUnavailable_OnServiceFailures(HttpStatusCode status, string body)
    {
        var (client, _) = Create(status, body);

        var result = await client.ParseValorantAsync([1], "s.png", "image/png", NoRosters, CancellationToken.None);

        result.Status.Should().Be(OcrCallStatus.Unavailable);
        result.ResultJson.Should().BeNull();
        result.UserMessage.Should().NotContain("boom");
    }

    [Fact]
    public async Task ParseValorantAsync_ReturnsUnavailable_WhenServiceTimesOut()
    {
        var handler = new StubHandler(async req =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = Build(handler, timeoutSeconds: 1);

        var result = await client.ParseValorantAsync([1], "s.png", "image/png", NoRosters, CancellationToken.None);

        result.Status.Should().Be(OcrCallStatus.Unavailable);
    }

    [Theory]
    [InlineData("", "token", false)]
    [InlineData("http://ocr:8095", "", false)]
    [InlineData("http://ocr:8095", "REPLACE_WITH_SCOREBOARD_OCR_TOKEN", false)]
    [InlineData("http://ocr:8095", "token", true)]
    public void IsConfigured_RequiresUrlAndRealToken(string url, string token, bool expected)
    {
        new ScoreboardOcrOptions { BaseUrl = url, ServiceToken = token }.IsConfigured.Should().Be(expected);
    }
}
