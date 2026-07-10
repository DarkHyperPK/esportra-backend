using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Services;

public sealed partial class GameCatalogService(
    IDbConnectionFactory db,
    IWebHostEnvironment env,
    ILogger<GameCatalogService> logger)
{
    private const string CatalogRelativePath = "GameCatalog/esportsGames.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ImportPackagedCatalogAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var hasAdminActive = await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.game_catalog_versions
                WHERE is_active = TRUE AND status = 'active' AND source = 'admin'
            )
            """);
        if (hasAdminActive)
        {
            logger.LogInformation("Skipping packaged catalog import — an admin-published catalog is active.");
            return;
        }

        var catalogPath = ResolveCatalogPath();
        if (!File.Exists(catalogPath))
            throw new FileNotFoundException("Packaged game catalog is missing.", catalogPath);

        var json = await File.ReadAllTextAsync(catalogPath, ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var catalogVersion = RequiredString(root, "catalogVersion");
        var schemaVersion = RequiredInt(root, "schemaVersion");
        var games = root.GetProperty("games").EnumerateArray().ToArray();
        if (games.Length == 0) throw new InvalidOperationException("Game catalog must contain at least one game.");

        ValidateCatalog(games);

        using var tx = conn.BeginTransaction();
        try
        {
            var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM public.game_catalog_versions WHERE content_hash = @hash LIMIT 1",
                new { hash }, tx);

            if (versionId is null)
            {
                versionId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO public.game_catalog_versions
                        (catalog_version, schema_version, content_hash, status, is_active)
                    VALUES (@catalogVersion, @schemaVersion, @hash, 'pending', FALSE)
                    RETURNING id
                    """,
                    new { catalogVersion, schemaVersion, hash }, tx);
            }
            else
            {
                await DeleteCatalogRowsAsync(conn, tx, versionId.Value);
            }

            for (var index = 0; index < games.Length; index++)
                await InsertGameAsync(conn, tx, versionId.Value, games[index], index);

            await conn.ExecuteAsync("UPDATE public.game_catalog_versions SET is_active = FALSE WHERE is_active = TRUE", transaction: tx);
            await conn.ExecuteAsync(
                """
                UPDATE public.game_catalog_versions
                SET status = 'active', is_active = TRUE, imported_at = NOW(), error_message = NULL
                WHERE id = @versionId
                """,
                new { versionId }, tx);

            var backfilled = await BackfillTournamentGameModesAsync(conn, tx, versionId.Value);

            tx.Commit();
            await BackfillActiveCatalogBannerUrlsAsync(ct);
            logger.LogInformation(
                "Game catalog {CatalogVersion} imported and activated ({Hash}); backfilled {TournamentCount} tournament game modes.",
                catalogVersion,
                hash,
                backfilled);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            logger.LogError(ex, "Game catalog import failed.");
            throw;
        }
    }

    public async Task<GameCatalogResponse> GetCatalogAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var version = await GetActiveVersionAsync(conn);
        if (version is null) throw new InvalidOperationException("No active game catalog version is available.");

        var games = (await conn.QueryAsync<GameRow>(
            """
            SELECT slug, name, category, game_type AS gameType, default_mode_key AS defaultModeKey,
                   features::text AS featuresJson, br_config::text AS brConfigJson,
                   logo_url AS logoUrl, icon_url AS iconUrl, cover_url AS coverUrl,
                   banner_url AS bannerUrl, sort_order AS sortOrder
            FROM public.game_catalog_games
            WHERE version_id = @versionId
            ORDER BY sort_order ASC, name ASC
            """,
            new { versionId = version.Id })).AsList();

        return new GameCatalogResponse(
            version.CatalogVersion,
            version.SchemaVersion,
            version.ContentHash,
            games.Select(g => BuildGameResponse(conn, version.Id, g)).ToArray());
    }

    public async Task<GameCatalogGameResponse?> GetGameAsync(string slugOrAlias, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var resolved = await ResolveGameAsync(conn, slugOrAlias);
        if (resolved is null) return null;

        var version = await GetActiveVersionAsync(conn);
        if (version is null) return null;

        return BuildGameResponse(conn, version.Id, resolved);
    }

    public async Task<TournamentCatalogResolution> ResolveTournamentAsync(
        string game,
        string? gameMode,
        int? teamSize,
        string? requestedStructure,
        string? tournamentType,
        IReadOnlyCollection<string>? stageStructures,
        bool hasMapPool,
        object? settings,
        IDbConnection? existingConnection = null,
        IDbTransaction? tx = null)
    {
        var ownsConnection = existingConnection is null;
        using var owned = ownsConnection ? db.CreateConnection() : null;
        var conn = existingConnection ?? owned!;

        var resolvedGame = await ResolveGameAsync(conn, game, tx)
            ?? throw new GameCatalogValidationException($"Unsupported game '{game}'.");

        var mode = await ResolveModeAsync(conn, resolvedGame.Slug, gameMode, teamSize, tx);
        var structure = await ResolveStructureAsync(conn, resolvedGame.Slug, requestedStructure, tournamentType, tx);

        if (teamSize.HasValue && teamSize.Value != mode.TeamSize)
            throw new GameCatalogValidationException($"Team size {teamSize.Value} does not match {resolvedGame.Name} mode '{mode.ModeKey}' ({mode.TeamSize}).");

        foreach (var stageStructure in stageStructures ?? Array.Empty<string>())
        {
            if (!await IsStructureSupportedAsync(conn, resolvedGame.Slug, stageStructure, tx))
                throw new GameCatalogValidationException($"{resolvedGame.Name} does not support tournament structure '{stageStructure}'.");
        }

        var features = ParseObject(resolvedGame.FeaturesJson);
        var mapPoolEnabled = features.TryGetProperty("mapPool", out var mapPool) && mapPool.ValueKind == JsonValueKind.True;
        if (hasMapPool && !mapPoolEnabled)
            throw new GameCatalogValidationException($"{resolvedGame.Name} does not support map pools.");

        var isBattleRoyale = resolvedGame.GameType == "battle_royale" || structure.StructureKey == "battle_royale";
        if (HasBattleRoyaleSettings(settings) && !isBattleRoyale)
            throw new GameCatalogValidationException($"{resolvedGame.Name} is not a battle royale game, but battle royale settings were provided.");

        return new TournamentCatalogResolution(
            resolvedGame.Name,
            resolvedGame.Slug,
            mode.ModeKey,
            mode.TeamSize,
            structure.StructureKey,
            EffectiveBoolFeature(features, mode.FeaturesOverrideJson, "mapVeto"));
    }

    public async Task<bool> SupportsMapVetoAsync(
        string game,
        string? gameMode,
        int? teamSize = null,
        IDbConnection? existingConnection = null,
        IDbTransaction? tx = null)
    {
        var ownsConnection = existingConnection is null;
        using var owned = ownsConnection ? db.CreateConnection() : null;
        var conn = existingConnection ?? owned!;

        var resolvedGame = await ResolveGameAsync(conn, game, tx)
            ?? throw new GameCatalogValidationException($"Unsupported game '{game}'.");

        var mode = await ResolveModeAsync(conn, resolvedGame.Slug, gameMode, teamSize, tx);
        var features = ParseObject(resolvedGame.FeaturesJson);
        return EffectiveBoolFeature(features, mode.FeaturesOverrideJson, "mapVeto");
    }

    public async Task<GameModeCatalogResolution> ResolveGameModeAsync(
        string game,
        string? gameMode,
        int? teamSize = null,
        IDbConnection? existingConnection = null,
        IDbTransaction? tx = null)
    {
        var ownsConnection = existingConnection is null;
        using var owned = ownsConnection ? db.CreateConnection() : null;
        var conn = existingConnection ?? owned!;

        var resolvedGame = await ResolveGameAsync(conn, game, tx)
            ?? throw new GameCatalogValidationException($"Unsupported game '{game}'.");

        var mode = await ResolveModeAsync(conn, resolvedGame.Slug, gameMode, teamSize, tx);
        if (teamSize.HasValue && teamSize.Value != mode.TeamSize)
            throw new GameCatalogValidationException($"Team size {teamSize.Value} does not match {resolvedGame.Name} mode '{mode.ModeKey}' ({mode.TeamSize}).");

        return new GameModeCatalogResolution(
            resolvedGame.Name,
            resolvedGame.Slug,
            mode.ModeKey,
            mode.Name,
            mode.TeamSize,
            mode.ParticipantMode,
            mode.AllowsSubstitutes,
            mode.MaxRosterSize,
            mode.MaxSubstitutes,
            mode.AllowsCoaches,
            mode.MaxCoaches,
            mode.ModeGroup,
            mode.VariantLabel);
    }

    public async Task<string> ResolveParticipantModeAsync(
        string game,
        string? gameMode,
        int? teamSize = null,
        IDbConnection? existingConnection = null,
        IDbTransaction? tx = null)
    {
        var mode = await ResolveGameModeAsync(game, gameMode, teamSize, existingConnection, tx);
        return mode.ParticipantMode;
    }

    public async Task<bool> TournamentUsesRosterPoolAsync(
        IDbConnection conn,
        IDbTransaction tx,
        Guid tournamentId)
    {
        var tournament = await conn.QuerySingleAsync<TournamentRegistrationCatalogRow>(
            """
            SELECT id, game, game_mode AS gameMode, team_size AS teamSize
            FROM public.tournaments
            WHERE id = @tournamentId
            """,
            new { tournamentId }, tx);

        var resolved = await ResolveTournamentAsync(
            tournament.Game,
            tournament.GameMode,
            tournament.TeamSize,
            null,
            null,
            Array.Empty<string>(),
            false,
            null,
            conn,
            tx);

        var mode = await ResolveModeAsync(conn, resolved.GameSlug, resolved.GameMode, resolved.TeamSize, tx);
        return UsesRosterPoolSelection(mode);
    }

    public async Task ValidateRegistrationAsync(
        IDbConnection conn,
        IDbTransaction tx,
        Guid tournamentId,
        Guid? teamId,
        Guid? rosterId,
        Guid userId,
        string? rosterLineupJson = null)
    {
        var tournament = await conn.QuerySingleAsync<TournamentRegistrationCatalogRow>(
            """
            SELECT id, game, game_mode AS gameMode, team_size AS teamSize
            FROM public.tournaments
            WHERE id = @tournamentId
            """,
            new { tournamentId }, tx);

        var resolved = await ResolveTournamentAsync(
            tournament.Game,
            tournament.GameMode,
            tournament.TeamSize,
            null,
            null,
            Array.Empty<string>(),
            false,
            null,
            conn,
            tx);

        var mode = await ResolveModeAsync(conn, resolved.GameSlug, resolved.GameMode, resolved.TeamSize, tx);
        if (mode.ParticipantMode == "solo")
        {
            if (teamId.HasValue)
                throw new GameCatalogValidationException("This tournament mode uses solo registration. Do not submit a team registration.");
            return;
        }

        if (!teamId.HasValue)
            throw new GameCatalogValidationException("This tournament requires a team registration.");
        if (!rosterId.HasValue)
            throw new GameCatalogValidationException("Select a roster that matches this tournament mode.");

        var team = await conn.QuerySingleOrDefaultAsync<TeamCatalogRow>(
            """
            SELECT id, name, game, game_format AS gameFormat, owner_id AS ownerId
            FROM public.teams
            WHERE id = @teamId AND is_active = TRUE
              AND COALESCE(team_kind, CASE WHEN COALESCE(is_solo, false) THEN 'solo' ELSE 'team' END) = 'team'
              AND COALESCE(is_solo, false) = false
              AND COALESCE(tag, '') NOT LIKE 'mock-%'
            """,
            new { teamId }, tx);
        if (team is null) throw new GameCatalogValidationException("Team was not found.");

        var canRegister = await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id = @teamId
                  AND user_id = @userId
                  AND is_active = TRUE
                  AND role IN ('captain'::public.team_member_role, 'owner'::public.team_member_role)
            ) OR @ownerId = @userId
            """,
            new { teamId, userId, team.OwnerId }, tx);
        if (!canRegister) throw new GameCatalogValidationException("Only a team captain or owner can register this team.");

        var roster = await conn.QuerySingleOrDefaultAsync<RosterCatalogRow>(
            """
            SELECT id, team_id AS teamId, game, format, team_size AS teamSize
            FROM public.team_rosters
            WHERE id = @rosterId AND team_id = @teamId
            """,
            new { rosterId, teamId }, tx);
        if (roster is null) throw new GameCatalogValidationException("Roster was not found for this team.");

        var rosterGameMatches = !string.IsNullOrWhiteSpace(roster.Game)
            ? StrictGameMatches(roster.Game, tournament.Game)
            : MatchesGame(team.Game, tournament.Game);
        if (!rosterGameMatches)
            throw new GameCatalogValidationException("Team roster game does not match this tournament.");

        var memberRows = (await conn.QueryAsync<RosterMemberCatalogRow>(
            """
            SELECT trm.user_id AS userId,
                   trm.roster_role::text AS rosterRole,
                   COALESCE(trm.is_starter, TRUE) AS isStarter,
                   tm.role::text AS teamMemberRole
            FROM public.team_roster_members trm
            JOIN public.team_rosters tr ON tr.id = trm.roster_id
            LEFT JOIN public.team_members tm
                ON tm.team_id = tr.team_id AND tm.user_id = trm.user_id AND tm.is_active = TRUE
            WHERE trm.roster_id = @rosterId
            """,
            new { rosterId }, tx)).AsList();

        var modeRules = new RosterModeRules(
            mode.TeamSize,
            mode.AllowsSubstitutes,
            mode.MaxRosterSize,
            mode.MaxSubstitutes,
            mode.AllowsCoaches,
            mode.MaxCoaches);

        if (UsesRosterPoolSelection(mode))
        {
            var gameRow = await ResolveGameAsync(conn, tournament.Game, tx)
                ?? throw new GameCatalogValidationException($"Unsupported game '{tournament.Game}'.");
            var poolMode = await ResolveModeAsync(conn, resolved.GameSlug, gameRow.DefaultModeKey, null, tx);

            if (!RosterMatchesPoolSource(poolMode, roster.Format))
                throw new GameCatalogValidationException(
                    $"Select a {poolMode.Name} roster to use as your player pool for this tournament.");

            var playerCount = memberRows.Count(IsRosterPlayer);
            var requiredPoolSize = mode.MaxRosterSize ?? mode.TeamSize;
            if (playerCount < requiredPoolSize)
                throw new GameCatalogValidationException(
                    $"Roster pool needs at least {requiredPoolSize} players (has {playerCount}).");

            if (string.IsNullOrWhiteSpace(rosterLineupJson))
                throw new GameCatalogValidationException("Select a tournament lineup from your roster pool.");

            IReadOnlyList<RosterLineupMember> lineupMembers;
            try
            {
                lineupMembers = RosterLineupSubmissionParser.ToValidatorMembers(
                    RosterLineupSubmissionParser.Parse(rosterLineupJson));
            }
            catch (InvalidOperationException ex)
            {
                throw new GameCatalogValidationException(ex.Message);
            }

            var rosterUserIds = memberRows.Select(row => row.UserId).ToHashSet();
            if (lineupMembers.Any(member => !rosterUserIds.Contains(member.UserId)))
                throw new GameCatalogValidationException(
                    "Tournament lineup includes a player who is not on the selected roster.");

            try
            {
                RosterLineupValidator.Validate(modeRules, lineupMembers);
            }
            catch (InvalidOperationException ex)
            {
                throw new GameCatalogValidationException(ex.Message);
            }

            return;
        }

        if (!ModeMatches(mode, roster.Format, roster.TeamSize))
            throw new GameCatalogValidationException($"Roster mode must match tournament mode '{mode.ModeKey}'.");

        try
        {
            RosterLineupValidator.Validate(
                modeRules,
                memberRows.Select(m => new RosterLineupMember(
                    m.UserId,
                    m.RosterRole ?? (m.IsStarter ? "starter" : "substitute"),
                    m.IsStarter,
                    m.TeamMemberRole)).ToList());
        }
        catch (InvalidOperationException ex)
        {
            throw new GameCatalogValidationException(ex.Message);
        }
    }

    private string ResolveCatalogPath()
    {
        var contentPath = Path.Combine(env.ContentRootPath, CatalogRelativePath);
        if (File.Exists(contentPath)) return contentPath;
        return Path.Combine(AppContext.BaseDirectory, CatalogRelativePath);
    }

    private static async Task DeleteCatalogRowsAsync(IDbConnection conn, IDbTransaction tx, Guid versionId)
    {
        await conn.ExecuteAsync("DELETE FROM public.game_catalog_game_aliases WHERE version_id = @versionId", new { versionId }, tx);
        await conn.ExecuteAsync("DELETE FROM public.game_catalog_tournament_structures WHERE version_id = @versionId", new { versionId }, tx);
        await conn.ExecuteAsync("DELETE FROM public.game_catalog_game_modes WHERE version_id = @versionId", new { versionId }, tx);
        await conn.ExecuteAsync("DELETE FROM public.game_catalog_games WHERE version_id = @versionId", new { versionId }, tx);
    }

    private static async Task<int> BackfillTournamentGameModesAsync(IDbConnection conn, IDbTransaction tx, Guid versionId) =>
        await conn.ExecuteAsync(
            """
            WITH resolved_game AS (
                SELECT DISTINCT ON (t.id)
                       t.id,
                       t.team_size,
                       g.slug,
                       g.default_mode_key
                FROM public.tournaments t
                JOIN public.game_catalog_game_aliases a
                  ON a.version_id = @versionId
                 AND LOWER(a.alias) = LOWER(t.game)
                JOIN public.game_catalog_games g
                  ON g.version_id = a.version_id
                 AND g.slug = a.game_slug
                WHERE t.game_mode IS NULL
                ORDER BY t.id,
                         CASE a.alias_type WHEN 'slug' THEN 0 WHEN 'name' THEN 1 ELSE 2 END
            ),
            unique_size_match AS (
                SELECT rg.id, MIN(m.mode_key) AS mode_key
                FROM resolved_game rg
                JOIN public.game_catalog_game_modes m
                  ON m.version_id = @versionId
                 AND m.game_slug = rg.slug
                 AND m.team_size = rg.team_size
                GROUP BY rg.id
                HAVING COUNT(*) = 1
            ),
            resolved_mode AS (
                SELECT rg.id, COALESCE(usm.mode_key, rg.default_mode_key) AS mode_key
                FROM resolved_game rg
                LEFT JOIN unique_size_match usm ON usm.id = rg.id
            )
            UPDATE public.tournaments t
            SET game_mode = resolved_mode.mode_key
            FROM resolved_mode
            WHERE t.id = resolved_mode.id
              AND t.game_mode IS NULL
            """,
            new { versionId }, tx);

    private async Task InsertGameAsync(IDbConnection conn, IDbTransaction tx, Guid versionId, JsonElement game, int sortIndex)
    {
        var slug = RequiredString(game, "slug");
        var name = RequiredString(game, "name");
        var type = RequiredString(game, "type");
        var modes = GetModes(game).ToArray();
        var defaultMode = OptionalString(game, "defaultMode") ?? OptionalString(game, "defaultFormat") ?? RequiredString(modes[0], "key");
        var featuresJson = game.TryGetProperty("features", out var features) ? features.GetRawText() : "{}";
        var brConfigJson = game.TryGetProperty("brConfig", out var brConfig) ? brConfig.GetRawText() : null;
        var logoUrl = OptionalString(game, "logo");
        var iconUrl = OptionalString(game, "icon");
        var coverUrl = OptionalString(game, "cover");
        var bannerUrl = ResolveBannerUrlForCatalogInsert(
            slug,
            name,
            OptionalString(game, "banner") ?? OptionalString(game, "bannerUrl"));
        var sortOrder = OptionalInt(game, "sortOrder") ?? sortIndex;

        await conn.ExecuteAsync(
            """
            INSERT INTO public.game_catalog_games
                (version_id, slug, name, category, game_type, default_mode_key,
                 features, br_config, raw, logo_url, icon_url, cover_url, banner_url, sort_order)
            VALUES
                (@versionId, @slug, @name, @category, @type, @defaultMode,
                 @featuresJson::jsonb, @brConfigJson::jsonb, @raw::jsonb,
                 @logoUrl, @iconUrl, @coverUrl, @bannerUrl, @sortOrder)
            """,
            new
            {
                versionId,
                slug,
                name,
                category = OptionalString(game, "category"),
                type,
                defaultMode,
                featuresJson,
                brConfigJson,
                raw = game.GetRawText(),
                logoUrl,
                iconUrl,
                coverUrl,
                bannerUrl,
                sortOrder,
            }, tx);

        foreach (var mode in modes)
        {
            var modeKey = RequiredString(mode, "key");
            await conn.ExecuteAsync(
                """
                INSERT INTO public.game_catalog_game_modes
                    (version_id, game_slug, mode_key, name, team_size, participant_mode,
                     allows_substitutes, max_roster_size, max_substitutes, allows_coaches, max_coaches,
                     aliases, display_group, variant_label,
                     map_pool_filter, features_override, raw)
                VALUES
                    (@versionId, @slug, @modeKey, @name, @teamSize, @participantMode,
                     @allowsSubstitutes, @maxRosterSize, @maxSubstitutes, @allowsCoaches, @maxCoaches,
                     @aliases, @displayGroup, @variantLabel,
                     @mapPoolFilter, @featuresOverrideJson::jsonb, @raw::jsonb)
                """,
                new
                {
                    versionId,
                    slug,
                    modeKey,
                    name = OptionalString(mode, "name") ?? modeKey,
                    teamSize = RequiredInt(mode, "teamSize"),
                    participantMode = OptionalString(mode, "participantMode") ?? "team",
                    allowsSubstitutes = OptionalBool(mode, "allowsSubstitutes") ?? true,
                    maxRosterSize = OptionalInt(mode, "maxRosterSize"),
                    maxSubstitutes = OptionalInt(mode, "maxSubstitutes"),
                    allowsCoaches = OptionalBool(mode, "allowsCoaches") ?? true,
                    maxCoaches = OptionalInt(mode, "maxCoaches") ?? 2,
                    aliases = ReadStringArray(mode, "aliases").Append(modeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    displayGroup = OptionalString(mode, "modeGroup"),
                    variantLabel = OptionalString(mode, "variantLabel"),
                    mapPoolFilter = NormalizeMapPoolFilter(OptionalString(mode, "mapPoolFilter")),
                    featuresOverrideJson = OptionalModeFeaturesJson(mode),
                    raw = mode.GetRawText()
                }, tx);
        }

        foreach (var structure in GetStructures(game))
        {
            var key = RequiredString(structure, "key");
            await conn.ExecuteAsync(
                """
                INSERT INTO public.game_catalog_tournament_structures
                    (version_id, game_slug, structure_key, name, is_default, raw)
                VALUES
                    (@versionId, @slug, @key, @name, @isDefault, @raw::jsonb)
                """,
                new
                {
                    versionId,
                    slug,
                    key,
                    name = OptionalString(structure, "name") ?? key,
                    isDefault = key == RequiredString(game.GetProperty("tournamentCapabilities"), "defaultStructure"),
                    raw = structure.GetRawText()
                }, tx);
        }

        var aliases = ReadStringArray(game, "aliases").Append(slug).Append(name).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.game_catalog_game_aliases (version_id, game_slug, alias, alias_type)
                VALUES (@versionId, @slug, @alias, @aliasType)
                ON CONFLICT DO NOTHING
                """,
                new
                {
                    versionId,
                    slug,
                    alias,
                    aliasType = string.Equals(alias, slug, StringComparison.OrdinalIgnoreCase) ? "slug"
                        : string.Equals(alias, name, StringComparison.OrdinalIgnoreCase) ? "name"
                        : "legacy"
                }, tx);
        }
    }

    private static void ValidateCatalog(JsonElement[] games)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var slug = RequiredString(game, "slug");
            if (!slugs.Add(slug)) throw new InvalidOperationException($"Duplicate game slug '{slug}'.");

            var type = RequiredString(game, "type");
            var modes = GetModes(game).ToArray();
            if (modes.Length == 0) throw new InvalidOperationException($"Game '{slug}' must define at least one mode.");

            var modeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mode in modes)
            {
                var key = RequiredString(mode, "key");
                if (!modeKeys.Add(key)) throw new InvalidOperationException($"Game '{slug}' has duplicate mode '{key}'.");
                var teamSize = RequiredInt(mode, "teamSize");
                var maxRoster = OptionalInt(mode, "maxRosterSize");
                if (maxRoster.HasValue && maxRoster.Value < teamSize)
                    throw new InvalidOperationException($"Game '{slug}' mode '{key}' maxRosterSize cannot be smaller than teamSize.");
            }

            var defaultMode = OptionalString(game, "defaultMode") ?? OptionalString(game, "defaultFormat");
            if (string.IsNullOrWhiteSpace(defaultMode) || !modeKeys.Contains(defaultMode))
                throw new InvalidOperationException($"Game '{slug}' has an invalid default mode.");

            var capabilities = game.GetProperty("tournamentCapabilities");
            var defaultStructure = RequiredString(capabilities, "defaultStructure");
            var structures = GetStructures(game).Select(s => RequiredString(s, "key")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!structures.Contains(defaultStructure))
                throw new InvalidOperationException($"Game '{slug}' has an invalid default tournament structure.");

            if (type == "battle_royale" && !game.TryGetProperty("brConfig", out _))
                throw new InvalidOperationException($"Battle royale game '{slug}' must define brConfig.");
        }
    }

    private static IEnumerable<JsonElement> GetModes(JsonElement game)
    {
        if (game.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array)
            return modes.EnumerateArray();

        return game.GetProperty("formats").EnumerateArray().Select(format =>
        {
            using var doc = JsonDocument.Parse($$"""
            {"key":{{JsonSerializer.Serialize(RequiredString(format, "value"))}},"name":{{JsonSerializer.Serialize(OptionalString(format, "name") ?? RequiredString(format, "value"))}},"teamSize":{{RequiredInt(format, "teamSize")}}}
            """);
            return doc.RootElement.Clone();
        }).ToArray();
    }

    private static IEnumerable<JsonElement> GetStructures(JsonElement game)
    {
        var capabilities = game.GetProperty("tournamentCapabilities");
        return capabilities.GetProperty("supportedStructures").EnumerateArray();
    }

    private static async Task<CatalogVersionRow?> GetActiveVersionAsync(IDbConnection conn) =>
        await conn.QuerySingleOrDefaultAsync<CatalogVersionRow>(
            """
            SELECT id AS Id, catalog_version AS CatalogVersion, schema_version AS SchemaVersion,
                   content_hash AS ContentHash, status AS Status, source AS Source
            FROM public.game_catalog_versions
            WHERE is_active = TRUE AND status = 'active'
            LIMIT 1
            """);

    private GameCatalogGameResponse BuildGameResponse(IDbConnection conn, Guid versionId, GameRow game)
    {
        var modes = conn.Query<ModeRow>(
            """
            SELECT mode_key AS modeKey, name, team_size AS teamSize, participant_mode AS participantMode,
                   allows_substitutes AS allowsSubstitutes, max_roster_size AS maxRosterSize,
                   max_substitutes AS maxSubstitutes, allows_coaches AS allowsCoaches, max_coaches AS maxCoaches,
                   aliases,
                   display_group AS modeGroup, variant_label AS variantLabel,
                   map_pool_filter AS mapPoolFilter, features_override::text AS featuresOverrideJson
            FROM public.game_catalog_game_modes
            WHERE version_id = @versionId AND game_slug = @slug
            ORDER BY COALESCE(display_group, name) ASC, team_size ASC, name ASC
            """,
            new { versionId, game.Slug }).AsList();
        EnrichModeRows(modes);

        var structures = conn.Query<StructureRow>(
            """
            SELECT structure_key AS structureKey, name, is_default AS isDefault
            FROM public.game_catalog_tournament_structures
            WHERE version_id = @versionId AND game_slug = @slug
            ORDER BY name ASC
            """,
            new { versionId, game.Slug }).AsList();

        var aliases = conn.Query<string>(
            """
            SELECT alias
            FROM public.game_catalog_game_aliases
            WHERE version_id = @versionId AND game_slug = @slug
            ORDER BY alias ASC
            """,
            new { versionId, game.Slug }).AsList();

        return new GameCatalogGameResponse(
            game.Slug,
            game.Name,
            game.Category,
            game.GameType,
            game.DefaultModeKey,
            ParseJson(game.FeaturesJson),
            BrCatalogBrConfigHelper.EnrichBrConfigForApi(game.BrConfigJson),
            game.LogoUrl,
            game.IconUrl,
            game.CoverUrl,
            game.BannerUrl,
            game.SortOrder,
            aliases,
            modes,
            structures);
    }

    private async Task<GameRow?> ResolveGameAsync(IDbConnection conn, string game, IDbTransaction? tx = null) =>
        await conn.QuerySingleOrDefaultAsync<GameRow>(
            """
            WITH active_version AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE AND status = 'active' LIMIT 1
            )
            SELECT g.slug, g.name, g.category, g.game_type AS gameType, g.default_mode_key AS defaultModeKey,
                   g.features::text AS featuresJson, g.br_config::text AS brConfigJson,
                   g.logo_url AS logoUrl, g.icon_url AS iconUrl, g.cover_url AS coverUrl,
                   g.banner_url AS bannerUrl, g.sort_order AS sortOrder
            FROM public.game_catalog_game_aliases a
            JOIN active_version av ON av.id = a.version_id
            JOIN public.game_catalog_games g ON g.version_id = a.version_id AND g.slug = a.game_slug
            WHERE LOWER(a.alias) = LOWER(@game)
            LIMIT 1
            """,
            new { game }, tx);

    private async Task<ModeRow> ResolveModeAsync(IDbConnection conn, string gameSlug, string? modeKey, int? teamSize, IDbTransaction? tx = null)
    {
        var modes = (await conn.QueryAsync<ModeRow>(
            """
            WITH active_version AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE AND status = 'active' LIMIT 1
            )
            SELECT m.mode_key AS modeKey, m.name, m.team_size AS teamSize, m.participant_mode AS participantMode,
                   m.allows_substitutes AS allowsSubstitutes, m.max_roster_size AS maxRosterSize,
                   m.max_substitutes AS maxSubstitutes, m.allows_coaches AS allowsCoaches, m.max_coaches AS maxCoaches,
                   m.aliases,
                   m.display_group AS modeGroup, m.variant_label AS variantLabel,
                   m.map_pool_filter AS mapPoolFilter, m.features_override::text AS featuresOverrideJson
            FROM public.game_catalog_game_modes m
            JOIN active_version av ON av.id = m.version_id
            WHERE m.game_slug = @gameSlug
            ORDER BY m.mode_key ASC
            """,
            new { gameSlug }, tx)).AsList();

        EnrichModeRows(modes);

        if (modes.Count == 0) throw new GameCatalogValidationException($"No game modes are configured for '{gameSlug}'.");

        if (!string.IsNullOrWhiteSpace(modeKey))
        {
            var explicitMode = modes.FirstOrDefault(m => ModeMatches(m, modeKey, teamSize));
            if (explicitMode is null) throw new GameCatalogValidationException($"Unsupported game mode '{modeKey}' for '{gameSlug}'.");
            return explicitMode;
        }

        if (teamSize.HasValue)
        {
            var bySize = modes.Where(m => m.TeamSize == teamSize.Value).ToList();
            if (bySize.Count == 1) return bySize[0];
            if (bySize.Count > 1)
                throw new GameCatalogValidationException($"Game mode is required for '{gameSlug}' because team size {teamSize.Value} matches multiple modes.");
        }

        return modes[0];
    }

    private async Task<StructureRow> ResolveStructureAsync(IDbConnection conn, string gameSlug, string? structureKey, string? tournamentType, IDbTransaction? tx = null)
    {
        var structures = (await conn.QueryAsync<StructureRow>(
            """
            WITH active_version AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE AND status = 'active' LIMIT 1
            )
            SELECT s.structure_key AS structureKey, s.name, s.is_default AS isDefault
            FROM public.game_catalog_tournament_structures s
            JOIN active_version av ON av.id = s.version_id
            WHERE s.game_slug = @gameSlug
            ORDER BY s.structure_key ASC
            """,
            new { gameSlug }, tx)).AsList();

        if (structures.Count == 0) throw new GameCatalogValidationException($"No tournament structures are configured for '{gameSlug}'.");

        var desired = !string.IsNullOrWhiteSpace(structureKey)
            ? structureKey
            : string.Equals(tournamentType, "battle_royale", StringComparison.OrdinalIgnoreCase)
                ? "battle_royale"
                : structures.FirstOrDefault(s => s.IsDefault)?.StructureKey ?? structures[0].StructureKey;

        return structures.FirstOrDefault(s => string.Equals(s.StructureKey, desired, StringComparison.OrdinalIgnoreCase))
            ?? throw new GameCatalogValidationException($"Unsupported tournament structure '{desired}' for '{gameSlug}'.");
    }

    private async Task<bool> IsStructureSupportedAsync(IDbConnection conn, string gameSlug, string structureKey, IDbTransaction? tx = null) =>
        await conn.QuerySingleAsync<bool>(
            """
            WITH active_version AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE AND status = 'active' LIMIT 1
            )
            SELECT EXISTS(
                SELECT 1
                FROM public.game_catalog_tournament_structures s
                JOIN active_version av ON av.id = s.version_id
                WHERE s.game_slug = @gameSlug AND LOWER(s.structure_key) = LOWER(@structureKey)
            )
            """,
            new { gameSlug, structureKey }, tx);

    private static bool MatchesGame(string? candidate, string expected) =>
        string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool StrictGameMatches(string candidate, string expected) =>
        string.Equals(candidate.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool UsesRosterPoolSelection(ModeRow mode) =>
        RosterPoolModeRules.UsesRosterPoolSelection(
            mode.ParticipantMode,
            mode.ModeKey,
            mode.ModeGroup,
            mode.MapPoolFilter);

    private static bool IsRosterPlayer(RosterMemberCatalogRow member)
    {
        var role = RosterLineupValidator.NormalizeRole(member.RosterRole, member.IsStarter);
        return role is "starter" or "substitute";
    }

    private static bool RosterMatchesPoolSource(ModeRow poolMode, string? rosterFormat) =>
        RosterPoolModeRules.RosterMatchesPoolSource(poolMode.ModeKey, rosterFormat, poolMode.Aliases);

    private static bool ModeMatches(ModeRow mode, string? candidateKey, int? teamSize)
    {
        if (teamSize.HasValue && mode.TeamSize != teamSize.Value) return false;
        if (string.IsNullOrWhiteSpace(candidateKey)) return true;
        return string.Equals(mode.ModeKey, candidateKey, StringComparison.OrdinalIgnoreCase)
            || (mode.Aliases ?? Array.Empty<string>()).Any(a => string.Equals(a, candidateKey, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasBattleRoyaleSettings(object? settings)
    {
        if (settings is null) return false;
        try
        {
            var json = JsonSerializer.Serialize(settings);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("brGameCount", out _)
                || doc.RootElement.TryGetProperty("brScoringPreset", out _)
                || doc.RootElement.TryGetProperty("brCustomScoring", out _)
                || doc.RootElement.TryGetProperty("brMultiStage", out _);
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement.Clone();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static bool EffectiveBoolFeature(JsonElement baseFeatures, string? modeOverrideJson, string featureName)
    {
        if (!string.IsNullOrWhiteSpace(modeOverrideJson))
        {
            try
            {
                using var overrideDoc = JsonDocument.Parse(modeOverrideJson);
                if (overrideDoc.RootElement.ValueKind == JsonValueKind.Object
                    && overrideDoc.RootElement.TryGetProperty(featureName, out var overrideValue)
                    && (overrideValue.ValueKind == JsonValueKind.True || overrideValue.ValueKind == JsonValueKind.False))
                {
                    return overrideValue.GetBoolean();
                }
            }
            catch
            {
                // Ignore malformed mode overrides; catalog validation handles persisted data quality.
            }
        }

        return baseFeatures.ValueKind == JsonValueKind.Object
            && baseFeatures.TryGetProperty(featureName, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static object ParseJson(string? json) => string.IsNullOrWhiteSpace(json)
        ? new Dictionary<string, object?>()
        : JsonSerializer.Deserialize<JsonElement>(json);

    private static string RequiredString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"Catalog field '{name}' is required.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int RequiredInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidOperationException($"Catalog field '{name}' must be a number.");
        return result;
    }

    private static int? OptionalInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;

    private static bool? OptionalBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : null;

    private static IEnumerable<string> ReadStringArray(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s))
            : Array.Empty<string>();

    private static string? NormalizeMapPoolFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Trim().ToLowerInvariant() switch
        {
            "standard" => "standard",
            "skirmish" => "skirmish",
            _ => throw new InvalidOperationException($"Invalid mapPoolFilter '{value}'. Use 'standard' or 'skirmish'.")
        };

    private static string? OptionalModeFeaturesJson(JsonElement mode)
    {
        if (!mode.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object)
            return null;
        return features.GetRawText();
    }

    internal static string? NormalizeMapPoolFilterOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "standard" => "standard",
            "skirmish" => "skirmish",
            _ => throw new GameCatalogValidationException($"Invalid mapPoolFilter '{value}'. Use 'standard' or 'skirmish'.")
        };
    }

    private static void EnrichModeRows(IEnumerable<ModeRow> modes)
    {
        foreach (var mode in modes)
        {
            if (string.IsNullOrWhiteSpace(mode.FeaturesOverrideJson))
            {
                mode.Features = null;
                continue;
            }

            var parsed = ParseJson(mode.FeaturesOverrideJson);
            mode.Features = parsed is System.Collections.IDictionary dict && dict.Count == 0 ? null : parsed;
        }
    }

    internal sealed class CatalogVersionRow
    {
        public Guid Id { get; set; }
        public string CatalogVersion { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string Status { get; set; } = "active";
        public string Source { get; set; } = "packaged";
    }

    internal sealed class GameRow
    {
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string GameType { get; set; } = string.Empty;
        public string DefaultModeKey { get; set; } = string.Empty;
        public string FeaturesJson { get; set; } = "{}";
        public string? BrConfigJson { get; set; }
        public string? LogoUrl { get; set; }
        public string? IconUrl { get; set; }
        public string? CoverUrl { get; set; }
        public string? BannerUrl { get; set; }
        public int SortOrder { get; set; }
    }
    public sealed class ModeRow
    {
        public string ModeKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int TeamSize { get; set; }
        public string ParticipantMode { get; set; } = "team";
        public bool AllowsSubstitutes { get; set; }
        public int? MaxRosterSize { get; set; }
        public int? MaxSubstitutes { get; set; }
        public bool AllowsCoaches { get; set; } = true;
        public int MaxCoaches { get; set; } = 2;
        public string[] Aliases { get; set; } = Array.Empty<string>();
        public string? ModeGroup { get; set; }
        public string? VariantLabel { get; set; }
        public string? MapPoolFilter { get; set; }

        [JsonIgnore]
        public string? FeaturesOverrideJson { get; set; }

        public object? Features { get; set; }
    }
    public sealed record StructureRow(string StructureKey, string Name, bool IsDefault);
    private sealed record TournamentRegistrationCatalogRow(Guid Id, string Game, string? GameMode, int? TeamSize);
    private sealed record TeamCatalogRow(Guid Id, string Name, string? Game, string? GameFormat, Guid OwnerId);
    private sealed record RosterCatalogRow(Guid Id, Guid TeamId, string? Game, string? Format, int TeamSize);
    private sealed record RosterMemberCatalogRow(Guid UserId, string? RosterRole, bool IsStarter, string? TeamMemberRole);
}

public sealed class GameCatalogValidationException(string message) : Exception(message);

public sealed record TournamentCatalogResolution(
    string GameName,
    string GameSlug,
    string GameMode,
    int TeamSize,
    string TournamentStructure,
    bool SupportsMapVeto);

public sealed record GameModeCatalogResolution(
    string GameName,
    string GameSlug,
    string GameMode,
    string ModeName,
    int TeamSize,
    string ParticipantMode,
    bool AllowsSubstitutes,
    int? MaxRosterSize,
    int? MaxSubstitutes,
    bool AllowsCoaches,
    int MaxCoaches,
    string? ModeGroup,
    string? VariantLabel);

public sealed record GameCatalogResponse(
    string CatalogVersion,
    int SchemaVersion,
    string ContentHash,
    IReadOnlyList<GameCatalogGameResponse> Games);

public sealed record GameCatalogGameResponse(
    string Slug,
    string Name,
    string? Category,
    string GameType,
    string DefaultModeKey,
    object Features,
    object BrConfig,
    string? Logo,
    string? Icon,
    string? Cover,
    string? Banner,
    int SortOrder,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<GameCatalogService.ModeRow> Modes,
    IReadOnlyList<GameCatalogService.StructureRow> TournamentStructures);

public sealed class GameCatalogVersionSummary
{
    public Guid Id { get; set; }
    public string CatalogVersion { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? PublishedBy { get; set; }
}
