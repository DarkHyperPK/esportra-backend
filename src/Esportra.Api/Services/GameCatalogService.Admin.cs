using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Requests;

namespace Esportra.Api.Services;

public sealed partial class GameCatalogService
{
    public async Task<GameCatalogResponse> GetOrCreateDraftAsync(Guid adminUserId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // Check for existing draft with FOR UPDATE lock to prevent race condition
            var draft = await conn.QuerySingleOrDefaultAsync<CatalogVersionRow>(
                """
                SELECT id AS Id, catalog_version AS CatalogVersion, schema_version AS SchemaVersion,
                       content_hash AS ContentHash, status AS Status, source AS Source
                FROM public.game_catalog_versions
                WHERE status = 'draft'
                LIMIT 1
                FOR UPDATE
                """, transaction: tx);

            if (draft is not null)
            {
                tx.Commit();
                return await BuildCatalogResponseForVersionAsync(conn, draft);
            }

            await CreateDraftFromActiveInternalAsync(conn, tx, adminUserId, ct);
            tx.Commit();

            draft = await GetDraftVersionAsync(conn)
                ?? throw new InvalidOperationException("Failed to create catalog draft.");
            return await BuildCatalogResponseForVersionAsync(conn, draft);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505"
            && ex.ConstraintName == "ux_game_catalog_versions_single_draft")
        {
            // Another request created the draft concurrently - read it instead
            tx.Rollback();
            using var freshConn = db.CreateConnection();
            var draft = await GetDraftVersionAsync(freshConn)
                ?? throw new InvalidOperationException("Draft creation race but no draft found.");
            return await BuildCatalogResponseForVersionAsync(freshConn, draft);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<GameCatalogResponse> ResetDraftFromActiveAsync(Guid adminUserId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await DiscardDraftInternalAsync(conn, tx);
            await CreateDraftFromActiveInternalAsync(conn, tx, adminUserId, ct);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        var draft = await GetDraftVersionAsync(conn)
            ?? throw new InvalidOperationException("Failed to reset catalog draft.");
        return await BuildCatalogResponseForVersionAsync(conn, draft);
    }

    public async Task<GameCatalogResponse> GetDraftAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var draft = await GetDraftVersionAsync(conn)
            ?? throw new GameCatalogValidationException("No catalog draft exists.");
        return await BuildCatalogResponseForVersionAsync(conn, draft);
    }

    public async Task<GameCatalogGameResponse> UpsertDraftGameAsync(string slug, UpsertDraftGameRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new GameCatalogValidationException("Game slug is required.");

        using var conn = db.CreateConnection();
        var draft = await GetDraftVersionAsync(conn)
            ?? throw new GameCatalogValidationException("No catalog draft exists. Create a draft first.");

        ValidateDraftGameRequest(slug, request);

        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_game_aliases WHERE version_id = @versionId AND game_slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_tournament_structures WHERE version_id = @versionId AND game_slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_game_modes WHERE version_id = @versionId AND game_slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_games WHERE version_id = @versionId AND slug = @slug",
                new { versionId = draft.Id, slug }, tx);

            var featuresJson = JsonSerializer.Serialize(request.Features, JsonOptions);
            var brConfigJson = request.BrConfig is null ? null : JsonSerializer.Serialize(request.BrConfig, JsonOptions);
            var raw = JsonSerializer.Serialize(request, JsonOptions);

            var bannerUrl = GameCatalogBannerSeed.NormalizeAbsoluteHttpsUrl(request.BannerUrl)
                ?? GameCatalogBannerSeed.TryResolve(slug, request.Name);

            await conn.ExecuteAsync(
                """
                INSERT INTO public.game_catalog_games
                    (version_id, slug, name, category, game_type, default_mode_key,
                     features, br_config, raw, logo_url, icon_url, cover_url, banner_url, sort_order)
                VALUES
                    (@versionId, @slug, @name, @category, @gameType, @defaultModeKey,
                     @featuresJson::jsonb, @brConfigJson::jsonb, @raw::jsonb,
                     @logoUrl, @iconUrl, @coverUrl, @bannerUrl, @sortOrder)
                """,
                new
                {
                    versionId = draft.Id,
                    slug,
                    name = request.Name,
                    category = request.Category,
                    gameType = request.GameType,
                    defaultModeKey = request.DefaultModeKey,
                    featuresJson,
                    brConfigJson,
                    raw,
                    logoUrl = request.LogoUrl,
                    iconUrl = request.IconUrl,
                    coverUrl = request.CoverUrl,
                    bannerUrl,
                    sortOrder = request.SortOrder,
                }, tx);

            foreach (var mode in request.Modes)
            {
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
                         @aliases, @modeGroup, @variantLabel,
                         @mapPoolFilter, @featuresOverrideJson::jsonb, @raw::jsonb)
                    """,
                    new
                    {
                        versionId = draft.Id,
                        slug,
                        modeKey = mode.ModeKey,
                        name = mode.Name,
                        teamSize = mode.TeamSize,
                        participantMode = mode.ParticipantMode,
                        allowsSubstitutes = mode.AllowsSubstitutes,
                        maxRosterSize = mode.MaxRosterSize,
                        maxSubstitutes = mode.MaxSubstitutes,
                        allowsCoaches = mode.AllowsCoaches,
                        maxCoaches = mode.MaxCoaches,
                        aliases = (mode.Aliases ?? Array.Empty<string>())
                            .Append(mode.ModeKey)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        modeGroup = mode.ModeGroup,
                        variantLabel = mode.VariantLabel,
                        mapPoolFilter = GameCatalogService.NormalizeMapPoolFilterOrNull(mode.MapPoolFilter),
                        featuresOverrideJson = mode.Features is null ? null : JsonSerializer.Serialize(mode.Features, JsonOptions),
                        raw = JsonSerializer.Serialize(mode, JsonOptions),
                    }, tx);
            }

            foreach (var structure in request.TournamentStructures)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.game_catalog_tournament_structures
                        (version_id, game_slug, structure_key, name, is_default, raw)
                    VALUES
                        (@versionId, @slug, @structureKey, @name, @isDefault, @raw::jsonb)
                    """,
                    new
                    {
                        versionId = draft.Id,
                        slug,
                        structureKey = structure.StructureKey,
                        name = structure.Name,
                        isDefault = structure.IsDefault,
                        raw = JsonSerializer.Serialize(structure, JsonOptions),
                    }, tx);
            }

            var aliases = (request.Aliases ?? Array.Empty<string>())
                .Append(slug)
                .Append(request.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase);

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
                        versionId = draft.Id,
                        slug,
                        alias,
                        aliasType = string.Equals(alias, slug, StringComparison.OrdinalIgnoreCase) ? "slug"
                            : string.Equals(alias, request.Name, StringComparison.OrdinalIgnoreCase) ? "name"
                            : "legacy",
                    }, tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        var game = await conn.QuerySingleAsync<GameRow>(
            """
            SELECT slug, name, category, game_type AS gameType, default_mode_key AS defaultModeKey,
                   features::text AS featuresJson, br_config::text AS brConfigJson,
                   logo_url AS logoUrl, icon_url AS iconUrl, cover_url AS coverUrl,
                   banner_url AS bannerUrl, sort_order AS sortOrder
            FROM public.game_catalog_games
            WHERE version_id = @versionId AND slug = @slug
            """,
            new { versionId = draft.Id, slug });

        return BuildGameResponse(conn, draft.Id, game);
    }

    public async Task DeleteDraftGameAsync(string slug, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var draft = await GetDraftVersionAsync(conn)
            ?? throw new GameCatalogValidationException("No catalog draft exists.");

        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_game_aliases WHERE version_id = @versionId AND game_slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_tournament_structures WHERE version_id = @versionId AND game_slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_game_modes WHERE version_id = @versionId AND game_slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM public.game_catalog_games WHERE version_id = @versionId AND slug = @slug",
                new { versionId = draft.Id, slug }, tx);
            if (deleted == 0)
                throw new GameCatalogValidationException($"Game '{slug}' was not found in the draft.");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task SetDraftGameLogoAsync(string slug, string logoUrl, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var draft = await GetDraftVersionAsync(conn)
            ?? throw new GameCatalogValidationException("No catalog draft exists.");

        var updated = await conn.ExecuteAsync(
            """
            UPDATE public.game_catalog_games
            SET logo_url = @logoUrl
            WHERE version_id = @versionId AND slug = @slug
            """,
            new { versionId = draft.Id, slug, logoUrl });

        if (updated == 0)
            throw new GameCatalogValidationException($"Game '{slug}' was not found in the draft.");
    }

    public async Task<GameCatalogResponse> PublishDraftAsync(Guid adminUserId, string? notes, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var draft = await conn.QuerySingleOrDefaultAsync<CatalogVersionRow>(
                """
                SELECT id AS Id, catalog_version AS CatalogVersion, schema_version AS SchemaVersion,
                       content_hash AS ContentHash, status AS Status, source AS Source
                FROM public.game_catalog_versions
                WHERE status = 'draft'
                LIMIT 1
                FOR UPDATE
                """,
                transaction: tx)
                ?? throw new GameCatalogValidationException("No catalog draft exists.");

            var draftCatalog = await BuildCatalogResponseForVersionAsync(conn, draft);
            await ValidateDraftCatalogAsync(draftCatalog);

            var hashInputs = await LoadHashInputsAsync(conn, draft.Id);
            var catalogVersion = $"{DateTime.UtcNow:yyyy.MM.dd}-admin-catalog";
            const int schemaVersion = 1;
            var contentHash = GameCatalogHashHelper.ComputeHash(catalogVersion, schemaVersion, hashInputs);

            await conn.ExecuteAsync(
                "UPDATE public.game_catalog_versions SET is_active = FALSE WHERE is_active = TRUE",
                transaction: tx);

            var activated = await conn.ExecuteAsync(
                """
                UPDATE public.game_catalog_versions
                SET catalog_version = @catalogVersion,
                    schema_version = @schemaVersion,
                    content_hash = @contentHash,
                    status = 'active',
                    is_active = TRUE,
                    source = 'admin',
                    published_by = @adminUserId,
                    published_at = NOW(),
                    imported_at = NOW(),
                    notes = @notes,
                    error_message = NULL
                WHERE id = @draftId AND status = 'draft'
                """,
                new
                {
                    catalogVersion,
                    schemaVersion,
                    contentHash,
                    adminUserId,
                    notes,
                    draftId = draft.Id,
                }, tx);

            if (activated == 0)
                throw new GameCatalogValidationException("Catalog draft was modified concurrently. Refresh and try again.");

            var backfilled = await BackfillTournamentGameModesAsync(conn, tx, draft.Id);
            tx.Commit();

            logger.LogInformation(
                "Game catalog draft published as {CatalogVersion} ({Hash}); backfilled {TournamentCount} tournaments.",
                catalogVersion,
                contentHash,
                backfilled);
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        using var readConn = db.CreateConnection();
        var active = await GetActiveVersionAsync(readConn)
            ?? throw new InvalidOperationException("Published catalog is missing.");
        return await BuildCatalogResponseForVersionAsync(readConn, active);
    }

    public async Task DiscardDraftAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await DiscardDraftInternalAsync(conn, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<GameCatalogVersionSummary>> GetVersionHistoryAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<GameCatalogVersionSummary>(
            """
            SELECT id AS Id,
                   catalog_version AS CatalogVersion,
                   schema_version AS SchemaVersion,
                   content_hash AS ContentHash,
                   status AS Status,
                   source AS Source,
                   is_active AS IsActive,
                   imported_at AS ImportedAt,
                   published_at AS PublishedAt,
                   created_by AS CreatedBy,
                   published_by AS PublishedBy
            FROM public.game_catalog_versions
            ORDER BY COALESCE(published_at, imported_at, created_at) DESC
            LIMIT 50
            """);
        return rows.AsList();
    }

    private async Task CreateDraftFromActiveAsync(IDbConnection conn, Guid adminUserId, CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            await CreateDraftFromActiveInternalAsync(conn, tx, adminUserId, ct);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task CreateDraftFromActiveInternalAsync(
        IDbConnection conn,
        IDbTransaction tx,
        Guid adminUserId,
        CancellationToken ct)
    {
        var existingDraft = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM public.game_catalog_versions WHERE status = 'draft' LIMIT 1", transaction: tx);
        if (existingDraft.HasValue)
            throw new GameCatalogValidationException("A catalog draft already exists.");

        var active = await conn.QuerySingleOrDefaultAsync<CatalogVersionRow>(
            """
            SELECT id AS Id, catalog_version AS CatalogVersion, schema_version AS SchemaVersion,
                   content_hash AS ContentHash, status AS Status, source AS Source
            FROM public.game_catalog_versions
            WHERE is_active = TRUE AND status = 'active'
            LIMIT 1
            """, transaction: tx);

        if (active is null)
        {
            // No active catalog exists - create an empty draft with unique hash
            await conn.ExecuteAsync(
                """
                INSERT INTO public.game_catalog_versions
                    (catalog_version, schema_version, content_hash, status, is_active, source, created_by)
                VALUES
                    (@catalogVersion, 1, @contentHash, 'draft', FALSE, 'admin', @adminUserId)
                """,
                new
                {
                    catalogVersion = $"{DateTime.UtcNow:yyyy.MM.dd}-draft",
                    contentHash = $"draft-{Guid.NewGuid():N}",
                    adminUserId,
                }, tx);
            return;
        }

        // Clone from active - use unique hash to avoid content_hash constraint collision
        await conn.ExecuteAsync(
            """
            INSERT INTO public.game_catalog_versions
                (catalog_version, schema_version, content_hash, status, is_active, source, created_by)
            VALUES
                (@catalogVersion, @schemaVersion, @contentHash, 'draft', FALSE, 'admin', @adminUserId)
            """,
            new
            {
                catalogVersion = $"{active.CatalogVersion}-draft",
                schemaVersion = active.SchemaVersion,
                contentHash = $"{active.ContentHash}-draft-{Guid.NewGuid():N}",
                adminUserId,
            }, tx);

        var draftId = await conn.QuerySingleAsync<Guid>(
            "SELECT id FROM public.game_catalog_versions WHERE status = 'draft' LIMIT 1", transaction: tx);

        await CloneVersionRowsAsync(conn, tx, active.Id, draftId);
    }

    private static async Task CloneVersionRowsAsync(IDbConnection conn, IDbTransaction tx, Guid sourceId, Guid targetId)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO public.game_catalog_games
                (version_id, slug, name, category, game_type, default_mode_key,
                 features, br_config, raw, logo_url, icon_url, cover_url, banner_url, sort_order)
            SELECT @targetId, slug, name, category, game_type, default_mode_key,
                   features, br_config, raw, logo_url, icon_url, cover_url, banner_url, sort_order
            FROM public.game_catalog_games
            WHERE version_id = @sourceId
            """,
            new { sourceId, targetId }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.game_catalog_game_modes
                (version_id, game_slug, mode_key, name, team_size, participant_mode,
                 allows_substitutes, max_roster_size, aliases, display_group, variant_label,
                 map_pool_filter, features_override, raw)
            SELECT @targetId, game_slug, mode_key, name, team_size, participant_mode,
                   allows_substitutes, max_roster_size, aliases, display_group, variant_label,
                   map_pool_filter, features_override, raw
            FROM public.game_catalog_game_modes
            WHERE version_id = @sourceId
            """,
            new { sourceId, targetId }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.game_catalog_tournament_structures
                (version_id, game_slug, structure_key, name, is_default, raw)
            SELECT @targetId, game_slug, structure_key, name, is_default, raw
            FROM public.game_catalog_tournament_structures
            WHERE version_id = @sourceId
            """,
            new { sourceId, targetId }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO public.game_catalog_game_aliases (version_id, game_slug, alias, alias_type)
            SELECT @targetId, game_slug, alias, alias_type
            FROM public.game_catalog_game_aliases
            WHERE version_id = @sourceId
            """,
            new { sourceId, targetId }, tx);
    }

    private static async Task DiscardDraftInternalAsync(IDbConnection conn, IDbTransaction tx)
    {
        var draftId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM public.game_catalog_versions WHERE status = 'draft' LIMIT 1", transaction: tx);
        if (draftId is null) return;

        await DeleteCatalogRowsAsync(conn, tx, draftId.Value);
        await conn.ExecuteAsync(
            "DELETE FROM public.game_catalog_versions WHERE id = @draftId",
            new { draftId }, tx);
    }

    private async Task<GameCatalogResponse> BuildCatalogResponseForVersionAsync(IDbConnection conn, CatalogVersionRow version)
    {
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

    private static async Task<CatalogVersionRow?> GetDraftVersionAsync(IDbConnection conn) =>
        await conn.QuerySingleOrDefaultAsync<CatalogVersionRow>(
            """
            SELECT id AS Id, catalog_version AS CatalogVersion, schema_version AS SchemaVersion,
                   content_hash AS ContentHash, status AS Status, source AS Source
            FROM public.game_catalog_versions
            WHERE status = 'draft'
            LIMIT 1
            """);

    private static async Task<List<CatalogGameHashInput>> LoadHashInputsAsync(IDbConnection conn, Guid versionId)
    {
        var games = (await conn.QueryAsync<GameRow>(
            """
            SELECT slug, name, category, game_type AS gameType, default_mode_key AS defaultModeKey,
                   features::text AS featuresJson, br_config::text AS brConfigJson,
                   logo_url AS logoUrl, icon_url AS iconUrl, cover_url AS coverUrl,
                   banner_url AS bannerUrl, sort_order AS sortOrder
            FROM public.game_catalog_games
            WHERE version_id = @versionId
            """,
            new { versionId })).AsList();

        var result = new List<CatalogGameHashInput>();
        foreach (var game in games)
        {
            var modes = (await conn.QueryAsync<ModeRow>(
                """
                SELECT mode_key AS modeKey, name, team_size AS teamSize, participant_mode AS participantMode,
                       allows_substitutes AS allowsSubstitutes, max_roster_size AS maxRosterSize, aliases,
                       display_group AS modeGroup, variant_label AS variantLabel,
                       map_pool_filter AS mapPoolFilter, features_override::text AS featuresOverrideJson
                FROM public.game_catalog_game_modes
                WHERE version_id = @versionId AND game_slug = @slug
                """,
                new { versionId, game.Slug })).AsList();
            EnrichModeRows(modes);

            var structures = (await conn.QueryAsync<StructureRow>(
                """
                SELECT structure_key AS structureKey, name, is_default AS isDefault
                FROM public.game_catalog_tournament_structures
                WHERE version_id = @versionId AND game_slug = @slug
                """,
                new { versionId, game.Slug })).AsList();

            var aliases = (await conn.QueryAsync<string>(
                """
                SELECT alias
                FROM public.game_catalog_game_aliases
                WHERE version_id = @versionId AND game_slug = @slug
                """,
                new { versionId, game.Slug })).AsList();

            result.Add(new CatalogGameHashInput(
                game.Slug,
                game.Name,
                game.Category,
                game.GameType,
                game.DefaultModeKey,
                ParseJson(game.FeaturesJson),
                string.IsNullOrWhiteSpace(game.BrConfigJson) ? null : ParseJson(game.BrConfigJson),
                game.LogoUrl,
                game.IconUrl,
                game.CoverUrl,
                game.SortOrder,
                modes.Select(m => new CatalogModeHashInput(
                    m.ModeKey, m.Name, m.TeamSize, m.ParticipantMode, m.AllowsSubstitutes,
                    m.MaxRosterSize, m.Aliases, m.ModeGroup, m.VariantLabel,
                    m.MapPoolFilter, m.Features)).ToArray(),
                structures.Select(s => new CatalogStructureHashInput(s.StructureKey, s.Name, s.IsDefault)).ToArray(),
                aliases));
        }

        return result;
    }

    private static readonly HashSet<string> AllowedStructureKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "single_elimination", "double_elimination", "swiss", "round_robin", "battle_royale",
    };

    private static void ValidateDraftGameRequest(string slug, UpsertDraftGameRequest request)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, @"^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            throw new GameCatalogValidationException($"Game slug '{slug}' must be lowercase alphanumeric with hyphens.");

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new GameCatalogValidationException($"Game '{slug}' must have a name.");

        if (request.Modes.Count == 0)
            throw new GameCatalogValidationException($"Game '{slug}' must define at least one mode.");

        var modeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in request.Modes)
        {
            if (!modeKeys.Add(mode.ModeKey))
                throw new GameCatalogValidationException($"Game '{slug}' has duplicate mode '{mode.ModeKey}'.");

            if (mode.TeamSize <= 0)
                throw new GameCatalogValidationException($"Game '{slug}' mode '{mode.ModeKey}' must have teamSize > 0.");

            if (mode.ParticipantMode is not ("team" or "solo"))
                throw new GameCatalogValidationException($"Game '{slug}' mode '{mode.ModeKey}' has invalid participantMode.");

            NormalizeMapPoolFilterOrNull(mode.MapPoolFilter);
        }

        if (!request.Modes.Any(m => string.Equals(m.ModeKey, request.DefaultModeKey, StringComparison.OrdinalIgnoreCase)))
            throw new GameCatalogValidationException($"Game '{slug}' has an invalid default mode.");

        if (request.TournamentStructures.Count == 0)
            throw new GameCatalogValidationException($"Game '{slug}' must define at least one tournament structure.");

        if (!request.TournamentStructures.Any(s => s.IsDefault))
            throw new GameCatalogValidationException($"Game '{slug}' must mark one tournament structure as default.");

        foreach (var structure in request.TournamentStructures)
        {
            if (!AllowedStructureKeys.Contains(structure.StructureKey))
                throw new GameCatalogValidationException($"Game '{slug}' has unsupported structure '{structure.StructureKey}'.");
        }

        if (request.GameType is not ("bracket" or "battle_royale"))
            throw new GameCatalogValidationException($"Game '{slug}' has invalid game type.");

        if (request.GameType == "battle_royale" && request.BrConfig is null)
            throw new GameCatalogValidationException($"Battle royale game '{slug}' must define brConfig.");
    }

    private static Task ValidateDraftCatalogAsync(GameCatalogResponse catalog)
    {
        if (catalog.Games.Count == 0)
            throw new GameCatalogValidationException("Catalog must contain at least one game.");

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in catalog.Games)
        {
            if (!slugs.Add(game.Slug))
                throw new GameCatalogValidationException($"Duplicate game slug '{game.Slug}'.");

            if (game.Modes.Count == 0)
                throw new GameCatalogValidationException($"Game '{game.Slug}' must define at least one mode.");

            var modeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mode in game.Modes)
            {
                if (!modeKeys.Add(mode.ModeKey))
                    throw new GameCatalogValidationException($"Game '{game.Slug}' has duplicate mode '{mode.ModeKey}'.");
            }

            if (!game.Modes.Any(m => string.Equals(m.ModeKey, game.DefaultModeKey, StringComparison.OrdinalIgnoreCase)))
                throw new GameCatalogValidationException($"Game '{game.Slug}' has an invalid default mode.");

            if (game.TournamentStructures.Count == 0)
                throw new GameCatalogValidationException($"Game '{game.Slug}' must define tournament structures.");

            if (game.GameType == "battle_royale" && game.BrConfig is null)
                throw new GameCatalogValidationException($"Battle royale game '{game.Slug}' must define brConfig.");

            foreach (var alias in game.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                if (globalAliases.TryGetValue(alias, out var owner) && !string.Equals(owner, game.Slug, StringComparison.OrdinalIgnoreCase))
                    throw new GameCatalogValidationException($"Alias '{alias}' is used by both '{owner}' and '{game.Slug}'.");
                globalAliases[alias] = game.Slug;
            }
        }

        return Task.CompletedTask;
    }
}
