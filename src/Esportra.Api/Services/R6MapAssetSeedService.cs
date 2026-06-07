using System.Net.Http.Headers;
using Dapper;
using Esportra.Core.Games;
using Esportra.Infrastructure.Database;

namespace Esportra.Api.Services;

/// <summary>
/// On startup, uploads bundled R6 map AVIFs to Supabase Storage and backfills game_maps.map_image_url
/// when Rainbow Six Siege rows are missing images.
/// </summary>
public sealed class R6MapAssetSeedService(
    IServiceProvider services,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<R6MapAssetSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SeedIfNeededAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "R6 map asset seed failed");
            }
        }, cancellationToken);

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedIfNeededAsync(CancellationToken ct)
    {
        var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
        var serviceKey = config["Supabase:ServiceKey"];

        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceKey))
        {
            logger.LogWarning("R6 map seed skipped: Supabase URL or service key not configured");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = db.CreateConnection();

        var missingCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM public.game_maps
            WHERE game = @game
              AND is_active = true
              AND (map_image_url IS NULL OR btrim(map_image_url) = '')
            """,
            new { game = R6MapCatalog.GameName });

        if (missingCount == 0)
        {
            logger.LogInformation("R6 map seed skipped: all active maps already have image URLs");
            return;
        }

        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "R6Maps");
        if (!Directory.Exists(assetsDir))
        {
            logger.LogWarning("R6 map seed skipped: bundled assets directory not found at {AssetsDir}", assetsDir);
            return;
        }

        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        client.DefaultRequestHeaders.Add("apikey", serviceKey);

        var uploaded = 0;
        var updated = 0;

        foreach (var (displayName, slug, filename) in R6MapCatalog.GetSeedEntries())
        {
            ct.ThrowIfCancellationRequested();

            var sourcePath = Path.Combine(assetsDir, filename);
            if (!File.Exists(sourcePath))
            {
                logger.LogWarning("R6 map seed skip missing file: {SourcePath}", sourcePath);
                continue;
            }

            var objectPath = R6MapCatalog.BuildObjectPath(slug);
            await using var stream = File.OpenRead(sourcePath);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/avif");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{supabaseUrl}/storage/v1/object/{R6MapCatalog.StorageBucket}/{objectPath}")
            {
                Content = content,
            };
            request.Headers.Add("x-upsert", "true");

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError(
                    "R6 map upload failed for {DisplayName}: {Status} {Body}",
                    displayName,
                    response.StatusCode,
                    body);
                continue;
            }

            uploaded++;

            var publicUrl = R6MapCatalog.BuildPublicUrl(supabaseUrl, slug)!;
            var rows = await conn.ExecuteAsync(
                """
                UPDATE public.game_maps
                SET map_image_url = @publicUrl
                WHERE game = @game
                  AND map_name = @displayName
                """,
                new
                {
                    publicUrl,
                    game = R6MapCatalog.GameName,
                    displayName,
                });

            if (rows > 0)
                updated++;
        }

        logger.LogInformation(
            "R6 map seed complete: uploaded={Uploaded} dbUpdated={Updated} missingBefore={Missing}",
            uploaded,
            updated,
            missingCount);
    }
}
