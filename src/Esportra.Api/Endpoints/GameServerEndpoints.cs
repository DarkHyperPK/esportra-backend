using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Integrations;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class GameServerEndpoints
{
    public static void MapGameServerEndpoints(this WebApplication app)
    {
        // ── GET /api/dathost/regions ─────────────────────────────────────────
        app.MapGet("/api/dathost/regions", () =>
        {
            var grouped = DatHostRegions.All
                .GroupBy(r => r.Continent)
                .Select(g => new
                {
                    continent = g.Key,
                    regions = g.Select(r => new
                    {
                        id = r.LocationId,
                        city = r.City,
                        country = r.Country,
                    })
                });
            return Results.Ok(grouped);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{matchId}/server ────────────────────────────────
        app.MapGet("/api/matches/{matchId}/server", async (
            Guid matchId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var server = await conn.QuerySingleOrDefaultAsync<dynamic>(
                @"SELECT id, match_id, provider, external_id, region, ip, raw_ip, port,
                         gotv_port, map, status, server_name, cost_per_hour,
                         started_at, stopped_at, created_at
                  FROM game_servers
                  WHERE match_id = @matchId AND deleted_at IS NULL
                  ORDER BY created_at DESC LIMIT 1",
                new { matchId });

            if (server is null)
                return Results.NotFound(new { error = "No server found for this match." });

            return Results.Ok(new
            {
                matchId,
                server = new
                {
                    id = (string)server.id.ToString(),
                    provider = (string)server.provider,
                    region = (string)server.region,
                    ip = (string?)server.ip,
                    rawIp = (string?)server.raw_ip,
                    port = (int?)server.port,
                    gotvPort = (int?)server.gotv_port,
                    map = (string?)server.map,
                    status = (string)server.status,
                    serverName = (string?)server.server_name,
                    costPerHour = server.cost_per_hour,
                    connectUrl = server.raw_ip != null && server.port != null
                        ? $"steam://connect/{server.raw_ip}:{server.port}"
                        : null,
                    startedAt = server.started_at?.ToString("o"),
                    createdAt = server.created_at.ToString("o"),
                }
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/server/provision ─────────────────────
        app.MapPost("/api/matches/{matchId}/server/provision", async (
            Guid matchId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IDatHostService dathost,
            IHubContext<Hubs.MatchHub> matchHub,
            IConfiguration config,
            ILogger<DatHostService> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Authorization: must be tournament organizer or platform admin
            var isAuthorized = await conn.QuerySingleOrDefaultAsync<bool>(
                @"SELECT EXISTS(
                    SELECT 1 FROM tournaments t
                    JOIN tournament_stages s ON s.tournament_id = t.id
                    JOIN brkt_versions v ON v.stage_id = s.id
                    JOIN brkt_matches m ON m.version_id = v.id
                    WHERE m.id = @matchId AND t.organizer_id = @userId
                  UNION ALL
                    SELECT 1 FROM admin_user_roles WHERE user_id = @userId
                )",
                new { matchId, userId = userCtx.UserIdGuid });
            if (!isAuthorized)
                return Results.Json(new { error = "Only tournament organizers or admins can provision servers." }, statusCode: 403);

            // Check if server already exists for this match (exclude failed — allow retry)
            var existing = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT status FROM game_servers WHERE match_id = @matchId AND deleted_at IS NULL AND status != 'failed' LIMIT 1",
                new { matchId });
            if (existing is not null)
                return Results.Conflict(new { error = "Server already provisioned for this match.", status = existing });

            // Get match + tournament details
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                @"SELECT m.id, m.team1_id, m.team2_id, m.status, m.version_id,
                         t.id as tournament_id, t.name as tournament_name, t.server_region, t.game
                  FROM brkt_matches m
                  JOIN brkt_versions v ON v.id = m.version_id
                  JOIN tournament_stages s ON s.id = v.stage_id
                  JOIN tournaments t ON t.id = s.tournament_id
                  WHERE m.id = @matchId",
                new { matchId });

            if (match is null)
                return Results.NotFound(new { error = "Match not found." });

            string region = (string?)match.server_region ?? "amsterdam";
            string game = (string?)match.game ?? "CS2";

            // Validate region is a known DatHost location
            if (!DatHostRegions.All.Any(r => r.LocationId == region))
                region = "amsterdam";

            // Only provision for CS2
            if (!game.Equals("CS2", StringComparison.OrdinalIgnoreCase) &&
                !game.Equals("Counter-Strike 2", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Server provisioning is only available for CS2." });

            // Get the first picked map from veto (if available)
            var startMap = await conn.QuerySingleOrDefaultAsync<string?>(
                @"SELECT picked_maps->0->>'map_id'
                  FROM match_map_vetos
                  WHERE match_id = @matchId AND status = 'completed'",
                new { matchId }) ?? "de_dust2";

            // Normalize map name
            if (!startMap.StartsWith("de_") && !startMap.StartsWith("cs_") && !startMap.StartsWith("ar_"))
                startMap = "de_" + startMap.ToLower().Replace(" ", "_");

            var gslt = config["DatHost:Gslt"] ?? "";
            var rconPassword = Guid.NewGuid().ToString("N")[..12];
            string tournamentName = ((string?)match.tournament_name) ?? "Tournament";
            var serverName = $"Esportra-{tournamentName[..Math.Min(20, tournamentName.Length)]}-M{matchId.ToString()[..8]}";

            try
            {
                var server = await dathost.CreateServerAsync(new DatHostCreateRequest
                {
                    Name = serverName,
                    Location = region,
                    RconPassword = rconPassword,
                    Gslt = gslt,
                    StartMap = startMap,
                    Slots = 12,
                    Tickrate = 128,
                    EnableGotv = true,
                }, ct);

                // Store in DB
                var serverId = await conn.QuerySingleAsync<Guid>(
                    @"INSERT INTO game_servers (match_id, tournament_id, provider, external_id, region, ip, raw_ip, port, gotv_port, rcon_password, map, status, cost_per_hour, server_name)
                      VALUES (@matchId, @tournamentId, 'dathost', @externalId, @region, @ip, @rawIp, @port, @gotvPort, @rcon, @map, 'provisioned', @cost, @name)
                      ON CONFLICT DO NOTHING
                      RETURNING id",
                    new
                    {
                        matchId,
                        tournamentId = (Guid?)match.tournament_id,
                        externalId = server.Id,
                        region,
                        ip = server.Ip,
                        rawIp = server.RawIp,
                        port = server.Ports?.Game,
                        gotvPort = server.Ports?.Gotv,
                        rcon = rconPassword,
                        map = startMap,
                        cost = server.CostPerHour,
                        name = serverName,
                    });

                // Start the server
                await dathost.StartServerAsync(server.Id, ct);

                // Update status
                await conn.ExecuteAsync(
                    "UPDATE game_servers SET status = 'starting', started_at = now() WHERE id = @serverId",
                    new { serverId });

                // Broadcast to match participants
                await matchHub.Clients.Group($"match:{matchId}")
                    .SendAsync("ServerProvisioned", new
                    {
                        matchId,
                        ip = server.RawIp,
                        port = server.Ports?.Game,
                        gotvPort = server.Ports?.Gotv,
                        map = startMap,
                        region,
                        connectUrl = $"steam://connect/{server.RawIp}:{server.Ports?.Game}",
                        status = "starting",
                    }, ct);

                logger.LogInformation("Server provisioned for match {MatchId}: {ServerId} at {Region}",
                    matchId, server.Id, region);

                return Results.Ok(new
                {
                    serverId = serverId.ToString(),
                    externalId = server.Id,
                    ip = server.RawIp,
                    port = server.Ports?.Game,
                    gotvPort = server.Ports?.Gotv,
                    map = startMap,
                    region,
                    connectUrl = $"steam://connect/{server.RawIp}:{server.Ports?.Game}",
                    status = "starting",
                });
            }
            catch (DatHostException ex)
            {
                logger.LogError(ex, "Failed to provision server for match {MatchId}", matchId);

                // Record the failure (mark deleted_at so it doesn't block retry)
                await conn.ExecuteAsync(
                    @"INSERT INTO game_servers (match_id, tournament_id, provider, external_id, region, status, error_message, server_name, deleted_at)
                      VALUES (@matchId, @tournamentId, 'dathost', '', @region, 'failed', @error, @name, now())",
                    new
                    {
                        matchId,
                        tournamentId = (Guid?)match.tournament_id,
                        region,
                        error = ex.Message,
                        name = serverName,
                    });

                return Results.Json(new { error = "Failed to provision server. Please try again." }, statusCode: 502);
            }
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/matches/{matchId}/server ─────────────────────────────
        app.MapDelete("/api/matches/{matchId}/server", async (
            Guid matchId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IDatHostService dathost,
            IHubContext<Hubs.MatchHub> matchHub,
            ILogger<DatHostService> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Authorization: must be tournament organizer or platform admin
            var isAuthorized = await conn.QuerySingleOrDefaultAsync<bool>(
                @"SELECT EXISTS(
                    SELECT 1 FROM tournaments t
                    JOIN tournament_stages s ON s.tournament_id = t.id
                    JOIN brkt_versions v ON v.stage_id = s.id
                    JOIN brkt_matches m ON m.version_id = v.id
                    WHERE m.id = @matchId AND t.organizer_id = @userId
                  UNION ALL
                    SELECT 1 FROM admin_user_roles WHERE user_id = @userId
                )",
                new { matchId, userId = userCtx.UserIdGuid });
            if (!isAuthorized)
                return Results.Json(new { error = "Only tournament organizers or admins can delete servers." }, statusCode: 403);

            var server = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, external_id, status FROM game_servers WHERE match_id = @matchId AND deleted_at IS NULL ORDER BY created_at DESC LIMIT 1",
                new { matchId });

            if (server is null)
                return Results.NotFound(new { error = "No server found for this match." });

            string externalId = server.external_id;
            if (!string.IsNullOrEmpty(externalId))
            {
                try { await dathost.DeleteServerAsync(externalId, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete DatHost server {Id}", externalId); }
            }

            await conn.ExecuteAsync(
                "UPDATE game_servers SET status = 'deleted', deleted_at = now(), stopped_at = now() WHERE id = @id",
                new { id = (Guid)server.id });

            await matchHub.Clients.Group($"match:{matchId}")
                .SendAsync("ServerDeleted", new { matchId }, ct);

            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Authenticated");
    }

    /// <summary>
    /// Called from VetoEndpoints when veto completes — provisions a server automatically.
    /// </summary>
    public static async Task AutoProvisionServerAsync(
        Guid matchId,
        IDbConnectionFactory db,
        IDatHostService dathost,
        IHubContext<Hubs.MatchHub> matchHub,
        IConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();

            // Check if already provisioned (exclude failed — allow retry)
            var existing = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT status FROM game_servers WHERE match_id = @matchId AND deleted_at IS NULL AND status != 'failed' LIMIT 1",
                new { matchId });
            if (existing is not null) return;

            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                @"SELECT m.id, t.id as tournament_id, t.name as tournament_name, t.server_region, t.game
                  FROM brkt_matches m
                  JOIN brkt_versions v ON v.id = m.version_id
                  JOIN tournament_stages s ON s.id = v.stage_id
                  JOIN tournaments t ON t.id = s.tournament_id
                  WHERE m.id = @matchId",
                new { matchId });

            if (match is null) return;

            string game = (string?)match.game ?? "";
            if (!game.Equals("CS2", StringComparison.OrdinalIgnoreCase) &&
                !game.Equals("Counter-Strike 2", StringComparison.OrdinalIgnoreCase))
                return;

            string region = (string?)match.server_region ?? "amsterdam";

            // Validate region is a known DatHost location
            if (!DatHostRegions.All.Any(r => r.LocationId == region))
                region = "amsterdam";

            var startMap = await conn.QuerySingleOrDefaultAsync<string?>(
                @"SELECT picked_maps->0->>'map_id'
                  FROM match_map_vetos WHERE match_id = @matchId AND status = 'completed'",
                new { matchId }) ?? "de_dust2";

            if (!startMap.StartsWith("de_") && !startMap.StartsWith("cs_") && !startMap.StartsWith("ar_"))
                startMap = "de_" + startMap.ToLower().Replace(" ", "_");

            var gslt = config["DatHost:Gslt"] ?? "";
            var rconPassword = Guid.NewGuid().ToString("N")[..12];
            var serverName = $"Esportra-M{matchId.ToString()[..8]}";

            var server = await dathost.CreateServerAsync(new DatHostCreateRequest
            {
                Name = serverName,
                Location = region,
                RconPassword = rconPassword,
                Gslt = gslt,
                StartMap = startMap,
                Slots = 12,
                Tickrate = 128,
                EnableGotv = true,
            }, ct);

            // INSERT with ON CONFLICT to prevent race-condition duplicates
            var serverId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                @"INSERT INTO game_servers (match_id, tournament_id, provider, external_id, region, ip, raw_ip, port, gotv_port, rcon_password, map, status, cost_per_hour, server_name, started_at)
                  VALUES (@matchId, @tournamentId, 'dathost', @externalId, @region, @ip, @rawIp, @port, @gotvPort, @rcon, @map, 'starting', @cost, @name, now())
                  ON CONFLICT (match_id) WHERE deleted_at IS NULL DO NOTHING
                  RETURNING id",
                new
                {
                    matchId,
                    tournamentId = (Guid?)match.tournament_id,
                    externalId = server.Id,
                    region,
                    ip = server.Ip,
                    rawIp = server.RawIp,
                    port = server.Ports?.Game,
                    gotvPort = server.Ports?.Gotv,
                    rcon = rconPassword,
                    map = startMap,
                    cost = server.CostPerHour,
                    name = serverName,
                });

            if (serverId is null)
            {
                // Race condition: another thread already inserted — clean up the DatHost server we just created
                try { await dathost.DeleteServerAsync(server.Id, ct); }
                catch { /* best-effort cleanup */ }
                logger.LogWarning("Auto-provision race detected for match {MatchId} — duplicate server cleaned up", matchId);
                return;
            }

            await dathost.StartServerAsync(server.Id, ct);

            await matchHub.Clients.Group($"match:{matchId}")
                .SendAsync("ServerProvisioned", new
                {
                    matchId,
                    ip = server.RawIp,
                    port = server.Ports?.Game,
                    gotvPort = server.Ports?.Gotv,
                    map = startMap,
                    region,
                    connectUrl = $"steam://connect/{server.RawIp}:{server.Ports?.Game}",
                    status = "starting",
                }, ct);

            logger.LogInformation("Auto-provisioned server for match {MatchId}: {ServerId} at {Region}", matchId, server.Id, region);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-provision failed for match {MatchId}", matchId);
        }
    }

    /// <summary>
    /// Called from MatchSystemEndpoints when match is finalized — deletes the server.
    /// </summary>
    public static async Task AutoDeleteServerAsync(
        Guid matchId,
        IDbConnectionFactory db,
        IDatHostService dathost,
        IHubContext<Hubs.MatchHub> matchHub,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();

            var server = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, external_id FROM game_servers WHERE match_id = @matchId AND deleted_at IS NULL AND status != 'failed' LIMIT 1",
                new { matchId });

            if (server is null) return;

            string externalId = server.external_id;
            if (!string.IsNullOrEmpty(externalId))
            {
                try { await dathost.DeleteServerAsync(externalId, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete DatHost server {Id} during auto-cleanup", externalId); }
            }

            await conn.ExecuteAsync(
                "UPDATE game_servers SET status = 'deleted', deleted_at = now(), stopped_at = now() WHERE id = @id",
                new { id = (Guid)server.id });

            await matchHub.Clients.Group($"match:{matchId}")
                .SendAsync("ServerDeleted", new { matchId }, ct);

            logger.LogInformation("Auto-deleted server for match {MatchId}", matchId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-delete failed for match {MatchId}", matchId);
        }
    }
}
