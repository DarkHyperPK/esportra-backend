using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time venue live status — replaces useVenueLiveStatus Supabase subscription.
/// Groups: venue:{venueId}
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<LiveHub> _logger;
    private readonly VenueConnectionTracker _tracker;
    private readonly IHubContext<VenueSyncHub> _syncHub;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public LiveHub(
        IDbConnectionFactory db,
        ILogger<LiveHub> logger,
        VenueConnectionTracker tracker,
        IHubContext<VenueSyncHub> syncHub)
    {
        _db = db;
        _logger = logger;
        _tracker = tracker;
        _syncHub = syncHub;
    }

    // ── Client-callable methods ───────────────────────────────────────────────

    public async Task JoinVenue(string venueId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, VenueGroup(venueId));
        _logger.LogDebug("Client {Conn} joined venue:{VenueId}", Context.ConnectionId, venueId);
    }

    public async Task LeaveVenue(string venueId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VenueGroup(venueId));

    // ═══════════════════════════════════════════════════════════════════════════
    //  FLOOR MAP — venue_stations
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<object[]> GetStationPositions(string venueId)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return []; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return []; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return []; }

        try
        {
            using var conn = _db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT station_id, pos_x AS x, pos_y AS y,
                       width, height, rotation, label, zone
                FROM venue_stations
                WHERE venue_id = @VenueId
                ORDER BY station_id
                """,
                new { VenueId = vid.Value });
            return rows.Select(r => (object)new
            {
                station_id = (string)r.station_id,
                x          = (decimal?)r.x ?? 0m,
                y          = (decimal?)r.y ?? 0m,
                width      = (decimal?)r.width ?? 1m,
                height     = (decimal?)r.height ?? 1m,
                rotation   = (decimal?)r.rotation ?? 0m,
                label      = (string?)r.label ?? "",
                zone       = (string?)r.zone,
            }).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStationPositions failed for venue {VenueId}", venueId);
            await SendError("Failed to load station positions.");
            return [];
        }
    }

    public async Task SaveStationPositions(string venueId, StationPositionDto[] positions)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return; }

        if (positions is null || positions.Length == 0)
        { await SendError("No positions provided."); return; }

        if (positions.Length > 500)
        { await SendError("Too many stations (max 500)."); return; }

        try
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            foreach (var p in positions)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO venue_stations (venue_id, station_id, pos_x, pos_y, width, height, rotation, label, zone)
                    VALUES (@VenueId, @StationId, @X, @Y, @Width, @Height, @Rotation, @Label, @Zone)
                    ON CONFLICT (venue_id, station_id)
                    DO UPDATE SET pos_x = @X, pos_y = @Y, width = @Width, height = @Height,
                                  rotation = @Rotation, label = @Label, zone = @Zone
                    """,
                    new
                    {
                        VenueId   = vid.Value,
                        StationId = p.station_id,
                        X         = p.x,
                        Y         = p.y,
                        Width     = p.width ?? 1m,
                        Height    = p.height ?? 1m,
                        Rotation  = p.rotation ?? 0m,
                        Label     = p.label ?? "",
                        Zone      = p.zone,
                    },
                    tx);
            }

            tx.Commit();

            await Clients.Group(VenueGroup(venueId))
                .SendAsync(LiveHubEvents.StationsUpdated, new { venueId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveStationPositions failed for venue {VenueId}", venueId);
            await SendError("Failed to save station positions.");
        }
    }

    public async Task RemoveStationPosition(string venueId, string stationId)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return; }

        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        try
        {
            using var conn = _db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                "DELETE FROM venue_stations WHERE venue_id = @VenueId AND station_id = @StationId",
                new { VenueId = vid.Value, StationId = stationId });

            if (rows == 0) { await SendError("Station not found."); return; }

            await Clients.Group(VenueGroup(venueId))
                .SendAsync(LiveHubEvents.StationsUpdated, new { venueId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveStationPosition failed for venue {VenueId}, station {StationId}", venueId, stationId);
            await SendError("Failed to remove station.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BILLING — venue_billing_config + venues.price_per_hour
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<object?> GetBillingConfig(string venueId)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        try
        {
            using var conn = _db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT v.price_per_hour,
                       COALESCE(bc.currency, v.currency, 'GBP') AS currency,
                       bc.zones,
                       bc.packages,
                       bc.vouchers,
                       bc.grace_period_minutes
                FROM venues v
                LEFT JOIN venue_billing_config bc ON bc.venue_id = v.id
                WHERE v.id = @VenueId AND v.deleted_at IS NULL
                """,
                new { VenueId = vid.Value });

            if (row is null) { await SendError("Venue not found."); return null; }

            return new
            {
                default_hourly_rate    = (decimal?)row.price_per_hour ?? 0m,
                default_daily_rate     = ((decimal?)row.price_per_hour ?? 0m) * 8m,
                currency               = (string?)row.currency ?? "GBP",
                grace_period_minutes   = (int?)row.grace_period_minutes ?? 5,
                zones                  = DeserializeJsonb<object[]>(row.zones) ?? Array.Empty<object>(),
                packages               = DeserializeJsonb<object[]>(row.packages) ?? Array.Empty<object>(),
                vouchers               = DeserializeJsonb<object[]>(row.vouchers) ?? Array.Empty<object>(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBillingConfig failed for venue {VenueId}", venueId);
            await SendError("Failed to load billing config.");
            return null;
        }
    }

    public async Task UpdateBillingConfig(string venueId, BillingConfigDto config)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return; }

        try
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            // Update venues.price_per_hour if provided
            if (config.default_hourly_rate.HasValue)
            {
                await conn.ExecuteAsync(
                    "UPDATE venues SET price_per_hour = @Rate, updated_at = NOW() WHERE id = @VenueId",
                    new { Rate = config.default_hourly_rate.Value, VenueId = vid.Value }, tx);
            }

            // Upsert billing config
            await conn.ExecuteAsync(
                """
                INSERT INTO venue_billing_config (venue_id, currency, zones, packages, vouchers, grace_period_minutes, updated_at)
                VALUES (@VenueId, @Currency, @Zones::jsonb, @Packages::jsonb, @Vouchers::jsonb, @GracePeriod, NOW())
                ON CONFLICT (venue_id) DO UPDATE SET
                    currency = COALESCE(@Currency, venue_billing_config.currency),
                    zones = COALESCE(@Zones::jsonb, venue_billing_config.zones),
                    packages = COALESCE(@Packages::jsonb, venue_billing_config.packages),
                    vouchers = COALESCE(@Vouchers::jsonb, venue_billing_config.vouchers),
                    grace_period_minutes = COALESCE(@GracePeriod, venue_billing_config.grace_period_minutes),
                    updated_at = NOW()
                """,
                new
                {
                    VenueId      = vid.Value,
                    Currency     = config.currency,
                    Zones        = config.zones is not null ? JsonSerializer.Serialize(config.zones, JsonOpts) : null,
                    Packages     = config.packages is not null ? JsonSerializer.Serialize(config.packages, JsonOpts) : null,
                    Vouchers     = config.vouchers is not null ? JsonSerializer.Serialize(config.vouchers, JsonOpts) : null,
                    GracePeriod  = config.grace_period_minutes,
                }, tx);

            tx.Commit();

            await Clients.Group(VenueGroup(venueId))
                .SendAsync(LiveHubEvents.BillingConfigUpdated, new { venueId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateBillingConfig failed for venue {VenueId}", venueId);
            await SendError("Failed to update billing config.");
        }
    }

    public async Task<object?> ApplyVoucher(string venueId, string code)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (string.IsNullOrWhiteSpace(code))
        { await SendError("Voucher code is required."); return null; }

        try
        {
            using var conn = _db.CreateConnection();
            var vouchersJson = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT vouchers::text FROM venue_billing_config WHERE venue_id = @VenueId",
                new { VenueId = vid.Value });

            if (vouchersJson is null)
                return new { valid = false, message = "No vouchers configured for this venue." };

            var vouchers = JsonSerializer.Deserialize<JsonElement[]>(vouchersJson, JsonOpts);
            if (vouchers is null || vouchers.Length == 0)
                return new { valid = false, message = "No vouchers configured for this venue." };

            var normalizedCode = code.Trim().ToUpperInvariant();
            foreach (var v in vouchers)
            {
                var voucherCode = v.TryGetProperty("code", out var c) ? c.GetString()?.ToUpperInvariant() : null;
                if (voucherCode == normalizedCode)
                {
                    var isActive = !v.TryGetProperty("is_active", out var a) || a.GetBoolean();
                    if (!isActive)
                        return new { valid = false, message = "This voucher is no longer active." };

                    var discount = v.TryGetProperty("discount", out var d) ? d.GetDecimal() : 0m;
                    var discountType = v.TryGetProperty("discount_type", out var dt) ? dt.GetString() : "percentage";
                    var description = v.TryGetProperty("description", out var desc) ? desc.GetString() : "";

                    return new
                    {
                        valid         = true,
                        message       = "Voucher applied successfully.",
                        code          = normalizedCode,
                        discount,
                        discount_type = discountType,
                        description,
                    };
                }
            }

            return new { valid = false, message = "Invalid voucher code." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyVoucher failed for venue {VenueId}, code {Code}", venueId, code);
            await SendError("Failed to validate voucher.");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ANALYTICS — venue_sessions, venue_bookings, station_health_snapshots
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<object?> GetAnalytics(string venueId, string from, string to)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
        { await SendError("Invalid date range. Use YYYY-MM-DD."); return null; }

        try
        {
            using var conn = _db.CreateConnection();
            var stats = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT
                  (SELECT COUNT(*) FROM venue_sessions
                   WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To) AS total_sessions,
                  (SELECT COALESCE(SUM(total_charged), 0) FROM venue_sessions
                   WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To) AS total_revenue,
                  (SELECT COUNT(*) FROM venue_bookings
                   WHERE venue_id = @VenueId AND booking_date >= @From::date AND booking_date < @To::date
                     AND status != 'cancelled') AS total_bookings,
                  (SELECT COUNT(*) FROM venue_bookings
                   WHERE venue_id = @VenueId AND booking_date >= @From::date AND booking_date < @To::date
                     AND status = 'cancelled') AS cancelled_bookings,
                  (SELECT COUNT(DISTINCT station_id) FROM venue_sessions
                   WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To) AS active_stations,
                  (SELECT COUNT(DISTINCT user_id) FROM venue_sessions
                   WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To
                     AND user_id IS NOT NULL) AS unique_users,
                  (SELECT COALESCE(AVG(EXTRACT(EPOCH FROM (COALESCE(ended_at, NOW()) - started_at)) / 60), 0)
                   FROM venue_sessions
                   WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To) AS avg_session_minutes
                """,
                new { VenueId = vid.Value, From = fromDate, To = toDate });

            return new
            {
                total_sessions     = (long)(stats?.total_sessions ?? 0L),
                total_revenue      = (decimal)(stats?.total_revenue ?? 0m),
                total_bookings     = (long)(stats?.total_bookings ?? 0L),
                cancelled_bookings = (long)(stats?.cancelled_bookings ?? 0L),
                active_stations    = (long)(stats?.active_stations ?? 0L),
                unique_users       = (long)(stats?.unique_users ?? 0L),
                avg_session_minutes = Math.Round((double)(stats?.avg_session_minutes ?? 0.0), 1),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAnalytics failed for venue {VenueId}", venueId);
            await SendError("Failed to load analytics.");
            return null;
        }
    }

    public async Task<object?> GetRevenueStats(string venueId, string from, string to)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
        { await SendError("Invalid date range. Use YYYY-MM-DD."); return null; }

        try
        {
            using var conn = _db.CreateConnection();
            var daily = (await conn.QueryAsync<dynamic>(
                """
                SELECT d::date AS date,
                       COALESCE(SUM(vs.total_charged), 0) AS revenue,
                       COUNT(vs.id) AS sessions
                FROM generate_series(@From::date, @To::date - INTERVAL '1 day', '1 day') d
                LEFT JOIN venue_sessions vs
                  ON vs.venue_id = @VenueId
                  AND vs.started_at >= d AND vs.started_at < d + INTERVAL '1 day'
                GROUP BY d::date
                ORDER BY d::date
                """,
                new { VenueId = vid.Value, From = fromDate, To = toDate })).ToArray();

            var byType = (await conn.QueryAsync<dynamic>(
                """
                SELECT session_type, COUNT(*) AS count, COALESCE(SUM(total_charged), 0) AS revenue
                FROM venue_sessions
                WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To
                GROUP BY session_type
                ORDER BY revenue DESC
                """,
                new { VenueId = vid.Value, From = fromDate, To = toDate })).ToArray();

            return new
            {
                daily = daily.Select(r => new
                {
                    date     = ((DateTime)r.date).ToString("yyyy-MM-dd"),
                    revenue  = (decimal)r.revenue,
                    sessions = (long)r.sessions,
                }).ToArray(),
                by_session_type = byType.Select(r => new
                {
                    session_type = (string)r.session_type,
                    count        = (long)r.count,
                    revenue      = (decimal)r.revenue,
                }).ToArray(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRevenueStats failed for venue {VenueId}", venueId);
            await SendError("Failed to load revenue stats.");
            return null;
        }
    }

    public async Task<object?> GetSessionHistory(string venueId, string from, string to, int page = 1, int pageSize = 20)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
        { await SendError("Invalid date range. Use YYYY-MM-DD."); return null; }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        try
        {
            using var conn = _db.CreateConnection();

            var total = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM venue_sessions
                WHERE venue_id = @VenueId AND started_at >= @From AND started_at < @To
                """,
                new { VenueId = vid.Value, From = fromDate, To = toDate });

            var rows = (await conn.QueryAsync<dynamic>(
                """
                SELECT vs.id, vs.station_id, vs.user_id, vs.session_type, vs.started_at,
                       vs.ended_at, vs.expires_at, vs.total_charged, vs.payment_method,
                       vs.display_name, vs.zone, vs.notes,
                       p.username AS user_name
                FROM venue_sessions vs
                LEFT JOIN profiles p ON p.id = vs.user_id
                WHERE vs.venue_id = @VenueId AND vs.started_at >= @From AND vs.started_at < @To
                ORDER BY vs.started_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { VenueId = vid.Value, From = fromDate, To = toDate, Limit = pageSize, Offset = offset })).ToArray();

            return new
            {
                items       = rows,
                total,
                page,
                page_size   = pageSize,
                total_pages = (int)Math.Ceiling((double)total / pageSize),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSessionHistory failed for venue {VenueId}", venueId);
            await SendError("Failed to load session history.");
            return null;
        }
    }

    public async Task<object[]> GetReportData(string venueId, string from, string to)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return []; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return []; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return []; }

        if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
        { await SendError("Invalid date range. Use YYYY-MM-DD."); return []; }

        try
        {
            using var conn = _db.CreateConnection();
            var rows = (await conn.QueryAsync<dynamic>(
                """
                SELECT d::date AS date,
                       COUNT(vs.id) AS sessions,
                       COALESCE(SUM(vs.total_charged), 0) AS revenue,
                       COUNT(DISTINCT vs.user_id) FILTER (WHERE vs.user_id IS NOT NULL) AS unique_users,
                       COALESCE(AVG(EXTRACT(EPOCH FROM (COALESCE(vs.ended_at, NOW()) - vs.started_at)) / 60), 0)
                         AS avg_duration_min,
                       (SELECT COUNT(*) FROM venue_bookings vb
                        WHERE vb.venue_id = @VenueId
                          AND vb.booking_date = d::date
                          AND vb.status != 'cancelled') AS bookings,
                       (SELECT AVG(sh.cpu_temp) FROM station_health_snapshots sh
                        WHERE sh.venue_id = @VenueId
                          AND sh.recorded_at >= d AND sh.recorded_at < d + INTERVAL '1 day') AS avg_cpu_temp
                FROM generate_series(@From::date, @To::date - INTERVAL '1 day', '1 day') d
                LEFT JOIN venue_sessions vs
                  ON vs.venue_id = @VenueId
                  AND vs.started_at >= d AND vs.started_at < d + INTERVAL '1 day'
                GROUP BY d::date
                ORDER BY d::date
                """,
                new { VenueId = vid.Value, From = fromDate, To = toDate })).ToArray();

            return rows.Select(r => (object)new
            {
                date            = ((DateTime)r.date).ToString("yyyy-MM-dd"),
                sessions        = (long)r.sessions,
                revenue         = (decimal)r.revenue,
                unique_users    = (long)r.unique_users,
                avg_duration_min = Math.Round((double)r.avg_duration_min, 1),
                bookings        = (long)r.bookings,
                avg_cpu_temp    = r.avg_cpu_temp is not null ? Math.Round((double)r.avg_cpu_temp, 1) : (double?)null,
            }).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetReportData failed for venue {VenueId}", venueId);
            await SendError("Failed to load report data.");
            return [];
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BOOKINGS — venue_bookings
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<object?> GetVenueBookings(string venueId, BookingFilterDto? filter)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        var date     = filter?.date;
        var status   = filter?.status;
        var page     = Math.Max(1, filter?.page ?? 1);
        var pageSize = Math.Clamp(filter?.pageSize ?? 20, 1, 100);
        var offset   = (page - 1) * pageSize;

        try
        {
            using var conn = _db.CreateConnection();

            var whereClauses = new List<string> { "vb.venue_id = @VenueId" };
            var parameters   = new DynamicParameters();
            parameters.Add("VenueId", vid.Value);

            if (!string.IsNullOrWhiteSpace(date))
            {
                whereClauses.Add("vb.booking_date = @BookingDate::date");
                parameters.Add("BookingDate", date);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                whereClauses.Add("vb.status = @Status");
                parameters.Add("Status", status);
            }

            var where = string.Join(" AND ", whereClauses);

            parameters.Add("Limit", pageSize);
            parameters.Add("Offset", offset);

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM venue_bookings vb WHERE {where}", parameters);

            var rows = (await conn.QueryAsync<dynamic>(
                $"""
                SELECT vb.id, vb.venue_id, vb.user_id, vb.booking_date, vb.start_time, vb.end_time,
                       vb.duration_hours, vb.stations_booked, vb.total_amount, vb.status,
                       vb.booking_code, vb.station_preference, vb.station_id,
                       vb.special_requests, vb.contact_phone, vb.contact_email,
                       vb.code_used_at, vb.cancelled_at, vb.cancellation_reason,
                       vb.created_at,
                       p.username AS user_name
                FROM venue_bookings vb
                LEFT JOIN profiles p ON p.id = vb.user_id
                WHERE {where}
                ORDER BY vb.booking_date DESC, vb.start_time DESC
                LIMIT @Limit OFFSET @Offset
                """, parameters)).ToArray();

            return new
            {
                items       = rows,
                total,
                page,
                page_size   = pageSize,
                total_pages = (int)Math.Ceiling((double)total / pageSize),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetVenueBookings failed for venue {VenueId}", venueId);
            await SendError("Failed to load bookings.");
            return null;
        }
    }

    public async Task CancelBooking(string bookingId, string? reason)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return; }

        var bid = ParseGuid(bookingId);
        if (bid is null) { await SendError("Invalid booking ID."); return; }

        try
        {
            using var conn = _db.CreateConnection();

            // Fetch booking + verify caller is venue owner/staff
            var booking = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT vb.id, vb.venue_id, vb.status, vb.code_used_at
                FROM venue_bookings vb
                WHERE vb.id = @BookingId
                """,
                new { BookingId = bid.Value });

            if (booking is null) { await SendError("Booking not found."); return; }

            Guid venueId = (Guid)booking.venue_id;
            if (!await IsVenueOwnerOrStaffAsync(userId, venueId))
            { await SendError("Forbidden."); return; }

            if ((string)booking.status == "cancelled")
            { await SendError("Booking is already cancelled."); return; }

            if (booking.code_used_at is not null)
            { await SendError("Cannot cancel — booking code already used."); return; }

            await conn.ExecuteAsync(
                """
                UPDATE venue_bookings
                SET status = 'cancelled', cancelled_at = NOW(), cancelled_by = @UserId,
                    cancellation_reason = @Reason, booking_code = NULL
                WHERE id = @BookingId
                """,
                new { BookingId = bid.Value, UserId = Guid.Parse(userId), Reason = reason });

            var venueIdStr = venueId.ToString();
            await Clients.Group(VenueGroup(venueIdStr))
                .SendAsync(LiveHubEvents.BookingChanged, new { venueId = venueIdStr, bookingId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CancelBooking failed for booking {BookingId}", bookingId);
            await SendError("Failed to cancel booking.");
        }
    }

    public async Task<object?> ValidateBookingCodeForAdmin(string venueId, string code)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        if (string.IsNullOrWhiteSpace(code))
        { await SendError("Booking code is required."); return null; }

        try
        {
            using var conn = _db.CreateConnection();
            var booking = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT vb.id, vb.venue_id, vb.user_id, vb.booking_date, vb.start_time, vb.end_time,
                       vb.duration_hours, vb.stations_booked, vb.total_amount, vb.status,
                       vb.booking_code, vb.station_preference, vb.code_used_at,
                       vb.contact_email, vb.contact_phone,
                       p.username AS user_name
                FROM venue_bookings vb
                LEFT JOIN profiles p ON p.id = vb.user_id
                WHERE vb.booking_code = UPPER(TRIM(@Code))
                  AND vb.venue_id = @VenueId
                """,
                new { Code = code, VenueId = vid.Value });

            if (booking is null)
                return new { valid = false, message = "Booking code not found." };

            if ((string)booking.status == "cancelled")
                return new { valid = false, message = "Booking has been cancelled." };

            if (booking.code_used_at is not null)
                return new { valid = false, message = "Booking code already used." };

            return new
            {
                valid   = true,
                message = "Valid booking code.",
                booking = new
                {
                    id               = ((Guid)booking.id).ToString(),
                    user_id          = booking.user_id?.ToString(),
                    user_name        = (string?)booking.user_name,
                    booking_date     = booking.booking_date,
                    start_time       = booking.start_time,
                    end_time         = booking.end_time,
                    duration_hours   = booking.duration_hours,
                    stations_booked  = booking.stations_booked,
                    total_amount     = booking.total_amount,
                    status           = (string)booking.status,
                    station_preference = (string?)booking.station_preference,
                    contact_email    = (string?)booking.contact_email,
                    contact_phone    = (string?)booking.contact_phone,
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateBookingCodeForAdmin failed for venue {VenueId}", venueId);
            await SendError("Failed to validate booking code.");
            return null;
        }
    }

    public async Task CheckInBooking(string bookingId)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return; }

        var bid = ParseGuid(bookingId);
        if (bid is null) { await SendError("Invalid booking ID."); return; }

        try
        {
            using var conn = _db.CreateConnection();
            var booking = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, venue_id, status, code_used_at FROM venue_bookings WHERE id = @BookingId",
                new { BookingId = bid.Value });

            if (booking is null) { await SendError("Booking not found."); return; }

            Guid venueId = (Guid)booking.venue_id;
            if (!await IsVenueOwnerOrStaffAsync(userId, venueId))
            { await SendError("Forbidden."); return; }

            if ((string)booking.status == "cancelled")
            { await SendError("Cannot check in a cancelled booking."); return; }

            if (booking.code_used_at is not null)
            { await SendError("Booking already checked in."); return; }

            await conn.ExecuteAsync(
                """
                UPDATE venue_bookings
                SET code_used_at = NOW(), status = 'completed'
                WHERE id = @BookingId
                """,
                new { BookingId = bid.Value });

            var venueIdStr = venueId.ToString();
            await Clients.Group(VenueGroup(venueIdStr))
                .SendAsync(LiveHubEvents.BookingChanged, new { venueId = venueIdStr, bookingId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckInBooking failed for booking {BookingId}", bookingId);
            await SendError("Failed to check in booking.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  OPENING HOURS — venues.opening_hours JSONB
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<object?> GetOpeningHours(string venueId)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        try
        {
            using var conn = _db.CreateConnection();
            var json = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT opening_hours::text FROM venues WHERE id = @VenueId AND deleted_at IS NULL",
                new { VenueId = vid.Value });

            if (json is null) { await SendError("Venue not found."); return null; }

            if (string.IsNullOrWhiteSpace(json) || json == "{}")
                return BuildDefaultOpeningHours();

            return DeserializeJsonb<object>(json) ?? BuildDefaultOpeningHours();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOpeningHours failed for venue {VenueId}", venueId);
            await SendError("Failed to load opening hours.");
            return null;
        }
    }

    public async Task UpdateOpeningHours(string venueId, object hours)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return; }

        if (hours is null)
        { await SendError("Opening hours data is required."); return; }

        try
        {
            var json = JsonSerializer.Serialize(hours, JsonOpts);

            using var conn = _db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                "UPDATE venues SET opening_hours = @Hours::jsonb, updated_at = NOW() WHERE id = @VenueId AND deleted_at IS NULL",
                new { Hours = json, VenueId = vid.Value });

            if (rows == 0) { await SendError("Venue not found."); return; }

            await Clients.Group(VenueGroup(venueId))
                .SendAsync(LiveHubEvents.OpeningHoursUpdated, new { venueId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateOpeningHours failed for venue {VenueId}", venueId);
            await SendError("Failed to update opening hours.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LOCAL HUB STATUS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check whether a venue's Local Hub is connected and return status info.
    /// Called by the web dashboard to show hub connectivity.
    /// </summary>
    public async Task<object?> GetHubStatus(string venueId)
    {
        var userId = GetUserId();
        if (userId is null) { await SendError("Not authenticated."); return null; }

        var vid = ParseGuid(venueId);
        if (vid is null) { await SendError("Invalid venue ID."); return null; }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        { await SendError("Forbidden."); return null; }

        var connInfo = _tracker.GetConnectionInfo(venueId);
        var isConnected = connInfo is not null;

        return new
        {
            is_connected = isConnected,
            connected_at = connInfo?.ConnectedAt,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  STATION COMMAND RELAY — Web Dashboard → Cloud → Local Hub
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task LockStation(string venueId, string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("LockStation", stationId);

        _logger.LogInformation("[LiveHub] LockStation relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    public async Task UnlockStation(string venueId, string stationId, object? session = null)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("UnlockStation", stationId, session);

        _logger.LogInformation("[LiveHub] UnlockStation relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    public async Task RestartStation(string venueId, string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("RestartStation", stationId);

        _logger.LogInformation("[LiveHub] RestartStation relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    public async Task ForceShutdown(string venueId, string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("ForceShutdown", stationId);

        _logger.LogInformation("[LiveHub] ForceShutdown relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    public async Task SendStationMessage(string venueId, string stationId, string message)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
        { await SendError("Message is required and must be under 500 characters."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("SendMessage", stationId, message);

        _logger.LogInformation("[LiveHub] SendMessage relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    public async Task SetStationMaintenance(string venueId, string stationId, bool maintenance)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("SetStationMaintenance", stationId, maintenance);

        _logger.LogInformation("[LiveHub] SetStationMaintenance relayed — venue={VenueId} station={StationId} maintenance={Maintenance}", venueId, stationId, maintenance);
    }

    public async Task EndSession(string venueId, string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("EndSession", stationId);

        _logger.LogInformation("[LiveHub] EndSession relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    public async Task ExtendSession(string venueId, string stationId, int additionalMinutes)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }
        if (additionalMinutes <= 0 || additionalMinutes > 1440)
        { await SendError("Additional minutes must be between 1 and 1440."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("ExtendSession", stationId, additionalMinutes);

        _logger.LogInformation("[LiveHub] ExtendSession relayed — venue={VenueId} station={StationId} minutes={Minutes}", venueId, stationId, additionalMinutes);
    }

    public async Task CreateSession(string venueId, string stationId, object sessionConfig)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        { await SendError("Station ID is required."); return; }

        var connectionId = await AuthorizeAndGetHubConnection(venueId);
        if (connectionId is null) return;

        await _syncHub.Clients.Client(connectionId)
            .SendAsync("CreateSession", stationId, sessionConfig);

        _logger.LogInformation("[LiveHub] CreateSession relayed — venue={VenueId} station={StationId}", venueId, stationId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string VenueGroup(string venueId) => $"venue:{venueId}";

    // ── Auth helpers ──────────────────────────────────────────────────────────

    private string? GetUserId() => Context.User?.FindFirst("sub")?.Value;

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var g) ? g : null;

    private async Task<bool> IsVenueOwnerOrStaffAsync(string userId, Guid venueId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM venues WHERE id = @VenueId AND owner_id = @UserId AND deleted_at IS NULL
                UNION ALL
                SELECT 1 FROM venue_staff WHERE venue_id = @VenueId AND user_id = @UserId AND accepted_at IS NOT NULL
            )
            """,
            new { VenueId = venueId, UserId = Guid.Parse(userId) });
    }

    private async Task SendError(string message) =>
        await Clients.Caller.SendAsync(LiveHubEvents.Error, message);

    // ── Station command relay helper ─────────────────────────────────────────

    /// <summary>
    /// Validates the caller is venue owner/staff and returns the Local Hub's
    /// connection ID, or null (with error sent to caller) if unauthorized or offline.
    /// </summary>
    private async Task<string?> AuthorizeAndGetHubConnection(string venueId)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            await SendError("Not authenticated.");
            return null;
        }

        var vid = ParseGuid(venueId);
        if (vid is null)
        {
            await SendError("Invalid venue ID.");
            return null;
        }

        if (!await IsVenueOwnerOrStaffAsync(userId, vid.Value))
        {
            await SendError("Forbidden.");
            return null;
        }

        var connectionId = _tracker.GetConnectionId(venueId);
        if (connectionId is null)
        {
            throw new HubException("Local Hub is offline");
        }

        return connectionId;
    }

    // ── Date parsing ──────────────────────────────────────────────────────────

    private static bool TryParseDate(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateTime.TryParseExact(value, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out result);
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static T? DeserializeJsonb<T>(object? value)
    {
        if (value is null || value is DBNull) return default;
        var str = value.ToString();
        if (string.IsNullOrWhiteSpace(str)) return default;
        try { return JsonSerializer.Deserialize<T>(str); }
        catch { return default; }
    }

    private static object BuildDefaultOpeningHours()
    {
        var defaultDay = new { is_open = true, open_time = "09:00", close_time = "23:00" };
        return new
        {
            monday    = defaultDay,
            tuesday   = defaultDay,
            wednesday = defaultDay,
            thursday  = defaultDay,
            friday    = defaultDay,
            saturday  = defaultDay,
            sunday    = defaultDay,
        };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════════════════════════════════════════

public sealed record StationPositionDto(
    string station_id,
    decimal x,
    decimal y,
    decimal? width,
    decimal? height,
    decimal? rotation,
    string? label,
    string? zone);

public sealed record BillingConfigDto(
    decimal? default_hourly_rate,
    string? currency,
    int? grace_period_minutes,
    object[]? zones,
    object[]? packages,
    object[]? vouchers);

public sealed record BookingFilterDto(
    string? date,
    string? status,
    int? page,
    int? pageSize);

/// <summary>Events broadcast to venue group clients.</summary>
public static class LiveHubEvents
{
    /// <summary>
    /// A station/seat changed state (available, occupied, locked, offline).
    /// Payload: { stationId, venueId, status, occupiedBy?, sessionStart? }
    /// </summary>
    public const string SeatUpdate = "SeatUpdate";

    /// <summary>
    /// Venue-level status changed (open, closed, full, maintenance).
    /// Payload: { venueId, status, updatedAt }
    /// </summary>
    public const string StatusChange = "StatusChange";

    /// <summary>
    /// A booking was created or cancelled — refresh availability calendar.
    /// Payload: { venueId, date, slotsAffected }
    /// </summary>
    public const string BookingChanged = "BookingChanged";

    /// <summary>Station positions were updated on the floor map.</summary>
    public const string StationsUpdated = "StationsUpdated";

    /// <summary>Billing config was updated.</summary>
    public const string BillingConfigUpdated = "BillingConfigUpdated";

    /// <summary>Opening hours were updated.</summary>
    public const string OpeningHoursUpdated = "OpeningHoursUpdated";

    /// <summary>Error message to the caller.</summary>
    public const string Error = "Error";
}
