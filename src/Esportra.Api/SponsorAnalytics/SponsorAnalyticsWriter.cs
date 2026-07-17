using System.Data;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;

namespace Esportra.Api.SponsorAnalytics;

public enum SponsorAnalyticsWriteResult
{
    Accepted,
    Duplicate,
    Invalid,
    SponsorNotFound,
}

public sealed class SponsorAnalyticsWriter(
    IDbConnectionFactory connectionFactory,
    SponsorAnalyticsIdentity identity,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AllowedPlacements = new(StringComparer.Ordinal)
    {
        "logo_ticker", "partner_showcase", "tournament_sidebar", "vertical_ad", "unknown",
    };

    public async Task<SponsorAnalyticsWriteResult> WriteAsync(
        RecordSponsorAnalyticsEventRequest request,
        UserContext? userContext,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!IsValidRequest(request)) return SponsorAnalyticsWriteResult.Invalid;

        var now = timeProvider.GetUtcNow();
        var demographics = await ResolveDemographicsAsync(userContext, context, now, cancellationToken);
        var identityKind = userContext is null ? "anonymous" : "authenticated";
        var identityMaterial = userContext?.UserId
            ?? $"{context.Connection.RemoteIpAddress}:{SponsorAnalyticsIdentity.CoarseClientClass(context.Request.Headers.UserAgent.ToString())}";
        var identityLookup = identity.CreateLookup(request.SponsorId, identityKind, identityMaterial);

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var sponsorExists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.sponsors WHERE id = @id AND is_active = TRUE)",
            new { id = request.SponsorId }, transaction);
        if (!sponsorExists) return SponsorAnalyticsWriteResult.SponsorNotFound;

        var audienceId = await ResolveAudienceIdAsync(
            connection,
            transaction,
            request.SponsorId,
            identityLookup,
            identityKind,
            now);

        var sequence = await connection.QuerySingleOrDefaultAsync<long?>(
            """
            INSERT INTO public.sponsor_analytics_events
                (event_id, sponsor_id, audience_id, identity_kind, event_type, placement,
                 tournament_id, page_path, country_code, country_provenance,
                 age_band, age_provenance, schema_version, received_at, event_date_utc)
            VALUES
                (@EventId, @SponsorId, @AudienceId, @IdentityKind, @EventType, @Placement,
                 @TournamentId, @PagePath, @CountryCode, @CountryProvenance,
                 @AgeBand, @AgeProvenance, @SchemaVersion, @ReceivedAt, @EventDate)
            ON CONFLICT (sponsor_id, event_id) DO NOTHING
            RETURNING event_sequence
            """,
            new
            {
                request.EventId,
                request.SponsorId,
                AudienceId = audienceId,
                IdentityKind = identityKind,
                request.EventType,
                request.Placement,
                request.TournamentId,
                PagePath = NormalizePagePath(request.PagePath),
                demographics.CountryCode,
                demographics.CountryProvenance,
                demographics.AgeBand,
                demographics.AgeProvenance,
                request.SchemaVersion,
                ReceivedAt = now,
                EventDate = DateOnly.FromDateTime(now.UtcDateTime),
            }, transaction);

        if (sequence is null)
        {
            transaction.Commit();
            return SponsorAnalyticsWriteResult.Duplicate;
        }

        await UpsertProjectionAsync(
            connection,
            transaction,
            request,
            audienceId,
            sequence.Value,
            demographics,
            DateOnly.FromDateTime(now.UtcDateTime));
        transaction.Commit();
        return SponsorAnalyticsWriteResult.Accepted;
    }

    private async Task<Guid> ResolveAudienceIdAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        Guid sponsorId,
        byte[] lookup,
        string kind,
        DateTimeOffset now)
    {
        var replacementAudienceId = Guid.NewGuid();
        var audienceId = await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.sponsor_audience_identities
                (sponsor_id, identity_lookup, identity_key_version, identity_kind,
                 audience_id, first_seen_at, last_seen_at, expires_at)
            VALUES
                (@sponsorId, @lookup, @keyVersion, @kind, @audienceId, @now, @now, @expiresAt)
            ON CONFLICT (sponsor_id, identity_lookup) DO UPDATE SET
                identity_key_version = EXCLUDED.identity_key_version,
                identity_kind = EXCLUDED.identity_kind,
                audience_id = CASE
                    WHEN sponsor_audience_identities.expires_at <= @now THEN EXCLUDED.audience_id
                    ELSE sponsor_audience_identities.audience_id
                END,
                first_seen_at = CASE
                    WHEN sponsor_audience_identities.expires_at <= @now THEN @now
                    ELSE sponsor_audience_identities.first_seen_at
                END,
                last_seen_at = @now,
                expires_at = @expiresAt
            RETURNING audience_id
            """,
            new
            {
                sponsorId,
                lookup,
                keyVersion = identity.KeyVersion,
                kind,
                audienceId = replacementAudienceId,
                now,
                expiresAt = now.AddDays(identity.LifetimeDays),
            }, transaction);
        return audienceId;
    }

    private static async Task UpsertProjectionAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        RecordSponsorAnalyticsEventRequest request,
        Guid audienceId,
        long sequence,
        DemographicSnapshot demographics,
        DateOnly factDate)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO public.sponsor_audience_daily_facts
                (sponsor_id, fact_date, audience_id, event_type, event_count,
                 first_event_sequence, last_event_sequence, country_code,
                 country_provenance, age_band, age_provenance)
            VALUES
                (@SponsorId, @FactDate, @AudienceId, @EventType, 1,
                 @Sequence, @Sequence, @CountryCode, @CountryProvenance,
                 @AgeBand, @AgeProvenance)
            ON CONFLICT (sponsor_id, fact_date, audience_id, event_type) DO UPDATE SET
                event_count = sponsor_audience_daily_facts.event_count + 1,
                last_event_sequence = EXCLUDED.last_event_sequence,
                country_code = COALESCE(EXCLUDED.country_code, sponsor_audience_daily_facts.country_code),
                country_provenance = CASE WHEN EXCLUDED.country_code IS NOT NULL
                    THEN EXCLUDED.country_provenance ELSE sponsor_audience_daily_facts.country_provenance END,
                age_band = COALESCE(EXCLUDED.age_band, sponsor_audience_daily_facts.age_band),
                age_provenance = CASE WHEN EXCLUDED.age_band IS NOT NULL
                    THEN EXCLUDED.age_provenance ELSE sponsor_audience_daily_facts.age_provenance END
            """,
            new
            {
                request.SponsorId,
                FactDate = factDate,
                AudienceId = audienceId,
                request.EventType,
                Sequence = sequence,
                demographics.CountryCode,
                demographics.CountryProvenance,
                demographics.AgeBand,
                demographics.AgeProvenance,
            }, transaction);

        await connection.ExecuteAsync(
            """
            INSERT INTO public.sponsor_daily_totals (sponsor_id, stat_date, impressions, clicks)
            VALUES (@SponsorId, @FactDate,
                    CASE WHEN @EventType = 'impression' THEN 1 ELSE 0 END,
                    CASE WHEN @EventType = 'click' THEN 1 ELSE 0 END)
            ON CONFLICT (sponsor_id, stat_date) DO UPDATE SET
                impressions = sponsor_daily_totals.impressions + EXCLUDED.impressions,
                clicks = sponsor_daily_totals.clicks + EXCLUDED.clicks
            """,
            new { request.SponsorId, FactDate = factDate, request.EventType }, transaction);
    }

    private async Task<DemographicSnapshot> ResolveDemographicsAsync(
        UserContext? userContext,
        HttpContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (userContext is not null)
        {
            using var connection = connectionFactory.CreateConnection();
            var profile = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
                "SELECT country_code AS CountryCode, date_of_birth AS DateOfBirth FROM public.profiles WHERE id = @id",
                new { id = userContext.UserIdGuid },
                cancellationToken: cancellationToken));
            var country = SponsorAnalyticsPolicy.NormalizeCountry(profile?.CountryCode);
            var age = SponsorAnalyticsPolicy.CalculateAgeBand(
                profile?.DateOfBirth,
                DateOnly.FromDateTime(now.UtcDateTime));
            return new DemographicSnapshot(
                country, country is null ? "unknown" : "profile_self_reported",
                age, age is null ? "unknown" : "profile_self_reported");
        }

        return new DemographicSnapshot(null, "unknown", null, "unknown");
    }

    internal static bool IsValidRequest(RecordSponsorAnalyticsEventRequest request) =>
        request.EventId != Guid.Empty
        && request.SponsorId != Guid.Empty
        && request.EventType is "impression" or "click"
        && AllowedPlacements.Contains(request.Placement)
        && request.SchemaVersion == 1
        && (request.PagePath is null || request.PagePath.Length <= 256);

    private static string? NormalizePagePath(string? pagePath)
    {
        if (string.IsNullOrWhiteSpace(pagePath)) return null;
        if (!Uri.TryCreate(pagePath, UriKind.Relative, out var parsed)) return null;
        var value = parsed.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped);
        return value.StartsWith('/') ? value : $"/{value}";
    }

    private sealed record ProfileRow(string? CountryCode, DateOnly? DateOfBirth);
    private sealed record DemographicSnapshot(
        string? CountryCode,
        string CountryProvenance,
        string? AgeBand,
        string AgeProvenance);
}
