using Dapper;
using Esportra.Contracts.Licensing;

namespace Esportra.Api.Endpoints;

internal static class ProEventPaymentConfiguration
{
    internal static async Task<ProEventPaymentDetails?> ReadAsync(IDbConnectionFactory db, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<Setting>(new CommandDefinition(
            "SELECT key AS Key, value AS Value FROM system_settings WHERE category = 'pro_event_payments'",
            cancellationToken: ct));
        var settings = rows.ToDictionary(row => row.Key, row => row.Value);
        string Value(string key) => settings.GetValueOrDefault($"pro_event.{key}", "").Trim();
        if (!bool.TryParse(Value("enabled"), out var enabled) || !enabled) return null;
        var seller = Value("seller_name"); var bank = Value("bank_name");
        var title = Value("account_title"); var account = Value("account_number");
        if (new[] { seller, bank, title, account }.Any(string.IsNullOrWhiteSpace)) return null;
        var qr = Value("qr_image_url");
        if (qr.Length > 0 && (!Uri.TryCreate(qr, UriKind.Absolute, out var uri) || uri.Scheme != "https")) return null;
        return new(seller, bank, title, account, Value("instructions"), qr.Length > 0 ? qr : null);
    }

    private sealed record Setting(string Key, string Value);
}
