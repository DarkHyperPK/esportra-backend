using System.Data;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>
/// Calculates session charges based on billing type, zone rates, and duration.
/// </summary>
public sealed class BillingService
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<BillingService> _logger;

    public BillingService(IDbConnectionFactory db, ILogger<BillingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Calculate the total charge for a completed session.
    /// </summary>
    public async Task<SessionBill> CalculateSessionBillAsync(
        Guid venueId, Guid sessionId, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT s.id, s.started_at, s.ended_at, s.expires_at,
                   s.session_type, s.rate_per_hour, s.total_charged,
                   s.zone, s.station_id, s.member_id, s.package_id,
                   z.hourly_rate AS zone_hourly_rate
            FROM venue_sessions s
            LEFT JOIN venue_stations vs ON vs.venue_id = s.venue_id AND vs.station_id = s.station_id
            LEFT JOIN zones z ON z.id = vs.zone_id
            WHERE s.id = @SessionId AND s.venue_id = @VenueId
            """,
            new { SessionId = sessionId, VenueId = venueId });

        if (session is null)
            return new SessionBill(0, 0, "Session not found", "free");

        var startedAt = (DateTimeOffset)session.started_at;
        var endedAt = session.ended_at is not null
            ? (DateTimeOffset)session.ended_at
            : DateTimeOffset.UtcNow;

        var durationMinutes = (int)Math.Ceiling((endedAt - startedAt).TotalMinutes);
        var sessionType = (string)session.session_type;

        // Get grace period from billing config
        var config = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT grace_period_minutes, currency FROM venue_billing_config WHERE venue_id = @VenueId",
            new { VenueId = venueId });

        var gracePeriod = (int?)(config?.grace_period_minutes) ?? 5;
        var currency = (string?)(config?.currency) ?? "GBP";

        return sessionType switch
        {
            "hourly" => CalculateHourly(session, durationMinutes, gracePeriod, currency),
            "package" => new SessionBill(0, durationMinutes, "Package session", "package"),
            "voucher" => new SessionBill(0, durationMinutes, "Voucher session", "voucher"),
            "complimentary" => new SessionBill(0, durationMinutes, "Complimentary session", "free"),
            "admin" => new SessionBill(0, durationMinutes, "Admin session", "free"),
            "web_booking" => CalculateHourly(session, durationMinutes, gracePeriod, currency),
            _ => CalculateHourly(session, durationMinutes, gracePeriod, currency),
        };
    }

    private static SessionBill CalculateHourly(
        dynamic session, int durationMinutes, int gracePeriod, string currency)
    {
        // Use session-level rate, fall back to zone rate
        decimal ratePerHour = (decimal?)(session.rate_per_hour)
            ?? (decimal?)(session.zone_hourly_rate)
            ?? 0m;

        // Apply grace period: if duration <= grace, no charge
        var billableMinutes = Math.Max(0, durationMinutes - gracePeriod);
        if (billableMinutes == 0)
            return new SessionBill(0, durationMinutes, $"Within {gracePeriod}min grace period", "free");

        // Pro-rate to the minute
        var total = Math.Round(ratePerHour * billableMinutes / 60m, 2);

        return new SessionBill(
            total,
            durationMinutes,
            $"{billableMinutes}min @ {currency} {ratePerHour}/hr",
            "hourly");
    }

    /// <summary>
    /// Create an invoice for a completed session.
    /// </summary>
    public async Task<Guid?> CreateSessionInvoiceAsync(
        Guid venueId, Guid sessionId, SessionBill bill,
        string? paymentMethod, Guid? createdBy, string? notes = null,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT station_id, member_id FROM venue_sessions WHERE id = @Id AND venue_id = @VenueId",
            new { Id = sessionId, VenueId = venueId });

        if (session is null) return null;

        var lineItems = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                type = "session",
                description = bill.Description,
                quantity = 1,
                unit_price = bill.Total,
                total = bill.Total,
            }
        });

        var invoiceId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO session_invoices
                (venue_id, session_id, member_id, station_id, line_items,
                 subtotal, total, payment_method, status, paid_at, created_by, notes)
            VALUES
                (@VenueId, @SessionId, @MemberId, @StationId, @LineItems::jsonb,
                 @Total, @Total, @PaymentMethod,
                 CASE WHEN @Total = 0 THEN 'paid' ELSE 'pending' END,
                 CASE WHEN @Total = 0 THEN NOW() ELSE NULL END,
                 @CreatedBy, @Notes)
            RETURNING id
            """,
            new
            {
                VenueId = venueId,
                SessionId = sessionId,
                MemberId = (Guid?)(session.member_id),
                StationId = (string)session.station_id,
                LineItems = lineItems,
                Total = bill.Total,
                PaymentMethod = bill.Total == 0 ? "free" : (paymentMethod ?? "cash"),
                CreatedBy = createdBy,
                Notes = notes,
            });

        return invoiceId;
    }
}

public record SessionBill(decimal Total, int DurationMinutes, string Description, string BillingType);
