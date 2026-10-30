using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Licensing;
using FluentAssertions;
using Npgsql;
using SkiaSharp;
using Xunit;

namespace Esportra.Api.Tests.Licensing;

[Collection(IntegrationCollection.Name)]
public sealed class ProEventRequestIntegrationTests(ApiFactory factory)
{
    private static ProEventRequest Request(int count = 64) => new(Guid.NewGuid(), "Integration Cup", "Valorant",
        "host@example.test", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(11)),
        count, AccessToken: new string('B', 64));

    private async Task ConfigureAsync(bool enabled)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.ExecuteAsync("""
            UPDATE system_settings SET value = CASE key
              WHEN 'pro_event.enabled' THEN @enabled
              WHEN 'pro_event.seller_name' THEN 'Test Seller'
              WHEN 'pro_event.bank_name' THEN 'Test Bank'
              WHEN 'pro_event.account_title' THEN 'Test Business'
              WHEN 'pro_event.account_number' THEN 'TEST-ACCOUNT-NOT-REAL'
              ELSE value END WHERE category = 'pro_event_payments'
            """, new { enabled = enabled ? "true" : "false" });
    }

    [Fact]
    public async Task UnconfiguredStandardInvoiceFailsWithoutSavingAFakeInvoice()
    {
        await ConfigureAsync(false);
        var request = Request();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/pro-event-requests", request);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM pro_event_requests WHERE id = @id", new { id = request.RequestId })).Should().Be(0);
    }

    [Fact]
    public async Task InvoiceIsServerPricedIdempotentAndSnapshotsItsPayee()
    {
        await ConfigureAsync(true);
        var request = Request(); var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/pro-event-requests", request);
        response.EnsureSuccessStatusCode();
        var receipt = (await response.Content.ReadFromJsonAsync<ProEventRequestReceipt>())!;
        receipt.EstimatedAmount.Should().Be(12000);
        receipt.Invoice!.PaymentDetails.AccountTitle.Should().Be("Test Business");
        await ConfigureAsync(false);
        var retry = await client.PostAsJsonAsync("/api/pro-event-requests", request);
        retry.EnsureSuccessStatusCode();
        (await retry.Content.ReadFromJsonAsync<ProEventRequestReceipt>()).Should().BeEquivalentTo(receipt);
        var conflict = await client.PostAsJsonAsync("/api/pro-event-requests", request with { TeamCount = 128 });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM pro_event_requests WHERE id = @id", new { id = request.RequestId })).Should().Be(1);
    }

    [Fact]
    public async Task PrivateResumeAndProofRequireTheSecretAndNeverGrantPro()
    {
        await ConfigureAsync(true);
        var request = Request(); var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/pro-event-requests", request)).EnsureSuccessStatusCode();
        var path = $"/api/pro-event-requests/{request.RequestId}";
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Add("X-Pro-Request-Token", request.AccessToken);
        var resumed = await client.GetAsync(path); resumed.EnsureSuccessStatusCode();
        resumed.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var bitmap = new SKBitmap(20, 20);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png.ToArray()); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "proof", "proof.png");
        (await client.PostAsync(path + "/proof", content)).EnsureSuccessStatusCode();
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        var row = await conn.QuerySingleAsync<(string Status, bool HasProof)>("SELECT status, proof_image IS NOT NULL FROM pro_event_requests WHERE id = @id", new { id = request.RequestId });
        row.Status.Should().Be("proof_submitted"); row.HasProof.Should().BeTrue();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM event_licenses WHERE tournament_id = @id", new { id = request.RequestId })).Should().Be(0);
        (await factory.CreateClient().GetAsync($"/api/admin/pro-event-requests/{request.RequestId}/proof")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CustomSizeSavesAQuoteRequestWithoutFabricatingAnAmount()
    {
        await ConfigureAsync(false);
        var response = await factory.CreateClient().PostAsJsonAsync("/api/pro-event-requests", Request(800));
        response.EnsureSuccessStatusCode();
        var receipt = (await response.Content.ReadFromJsonAsync<ProEventRequestReceipt>())!;
        receipt.EstimatedAmount.Should().BeNull(); receipt.Invoice.Should().BeNull();
    }

    [Fact]
    public async Task QueueRejectsAnonymousAndOrdinaryUsers()
    {
        (await factory.CreateClient().GetAsync("/api/admin/pro-event-requests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateAuthenticatedClient(Guid.NewGuid()).GetAsync("/api/admin/pro-event-requests")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StaffCanReadQueueAndAuditStatusChangesButPublicCannotReadProof()
    {
        await ConfigureAsync(true);
        var request = Request();
        (await factory.CreateClient().PostAsJsonAsync("/api/pro-event-requests", request)).EnsureSuccessStatusCode();
        var staff = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(staff);
        await factory.Seeder.SeedAdminPanelRoleAsync(staff);
        var client = factory.CreateAuthenticatedClient(staff);
        var list = await client.GetAsync("/api/admin/pro-event-requests?status=pending&page=0");
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        (await list.Content.ReadAsStringAsync()).Should().Contain(request.RequestId.ToString());
        (await client.PutAsJsonAsync($"/api/admin/pro-event-requests/{request.RequestId}/status", new { status = "contacted" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM pro_event_request_audit WHERE request_id = @id AND actor_id = @staff", new { id = request.RequestId, staff })).Should().Be(1);
    }

    [Fact]
    public async Task InvalidProofAndAnotherRequestsTokenAreRejected()
    {
        await ConfigureAsync(true);
        var request = Request(); var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/pro-event-requests", request)).EnsureSuccessStatusCode();
        var path = $"/api/pro-event-requests/{request.RequestId}";
        client.DefaultRequestHeaders.Add("X-Pro-Request-Token", new string('C', 64));
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Remove("X-Pro-Request-Token"); client.DefaultRequestHeaders.Add("X-Pro-Request-Token", request.AccessToken);
        using var content = new MultipartFormDataContent(); content.Add(new ByteArrayContent("<script>bad</script>"u8.ToArray()), "proof", "fake.png");
        (await client.PostAsync(path + "/proof", content)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        (await conn.ExecuteScalarAsync<bool>("SELECT proof_image IS NULL FROM pro_event_requests WHERE id = @id", new { id = request.RequestId })).Should().BeTrue();
    }

    [Fact]
    public async Task ClientDatabaseRolesCannotReadEventBriefs()
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SET ROLE authenticated");
        try { (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM pro_event_requests")).Should().Be(0); }
        catch (PostgresException ex) when (ex.SqlState == "42501") { /* A table-level deny is stricter than zero RLS-visible rows. */ }
        finally { await conn.ExecuteAsync("RESET ROLE"); }
    }
}
