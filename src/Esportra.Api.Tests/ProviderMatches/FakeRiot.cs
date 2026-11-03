using System.Net;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Infrastructure.Integrations;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Esportra.Api.Tests.ProviderMatches;

/// <summary>A scripted Riot API and Hangfire client, so the ingest job runs end to end without the network.</summary>
internal sealed class FakeRiot : HttpMessageHandler, IBackgroundJobClient
{
    public string? MatchBody { get; set; }
    public HttpStatusCode MatchStatus { get; set; } = HttpStatusCode.OK;
    public int Requests { get; private set; }
    public int Scheduled { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("/active-shards/"))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"activeShard\":\"eu\"}") });
        var status = MatchBody is null && MatchStatus == HttpStatusCode.OK ? HttpStatusCode.NotFound : MatchStatus;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(MatchBody ?? "{}") });
    }

    public async Task RunJobAsync(ApiFactory factory, Guid associationId, int attempt)
    {
        var riot = new RiotApiClient(new HttpClient(this), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Riot:ApiKey"] = "test-key" }).Build());
        var collection = new ServiceCollection();
        collection.AddHybridCache();
        using var services = collection.BuildServiceProvider();
        var job = new ProviderMatchIngestJob(
            factory.Db, riot, services.GetRequiredService<HybridCache>(), this,
            Microsoft.Extensions.Options.Options.Create(new Esportra.Api.Services.ProviderMatches.RiotMatchOptions()),
            NullLogger<ProviderMatchIngestJob>.Instance);
        await job.RunAsync(associationId, attempt, CancellationToken.None);
    }

    public string Create(Job job, IState state)
    {
        if (state is ScheduledState) Scheduled++;
        return "1";
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
