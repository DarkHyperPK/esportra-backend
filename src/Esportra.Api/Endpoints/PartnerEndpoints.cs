using Dapper;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Partner application + public partner listing.
/// Replaces usePartnerApplication.ts Supabase insert.
/// </summary>
public static class PartnerEndpoints
{
    public static void MapPartnerEndpoints(this WebApplication app)
    {
        // ── POST /api/partners/apply ─────────────────────────────────────────
        // Public endpoint — anyone can submit a partner application (matches RLS: anon INSERT)
        app.MapPost("/api/partners/apply", async (
            [FromBody] PartnerApplicationRequest req,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // Duplicate check by email
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM partner_applications WHERE contact_email = @email AND status NOT IN ('rejected', 'archived') LIMIT 1",
                new { email = req.ContactEmail });

            if (existing is not null)
                return Results.Conflict(new { error = "An application with this email already exists." });

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO partner_applications
                    (company_name, company_website, company_size, industry,
                     contact_name, contact_email, contact_phone, contact_title,
                     partnership_tier, partnership_goals, budget_range, message, how_heard)
                VALUES
                    (@companyName, @companyWebsite, @companySize, @industry,
                     @contactName, @contactEmail, @contactPhone, @contactTitle,
                     @partnershipTier, @partnershipGoals::text[], @budgetRange, @message, @howHeard)
                RETURNING id, company_name, company_website, company_size, industry,
                         contact_name, contact_email, contact_phone, contact_title,
                         partnership_tier, partnership_goals, budget_range, message, how_heard,
                         status, created_at
                """,
                new
                {
                    companyName      = req.CompanyName,
                    companyWebsite   = req.CompanyWebsite,
                    companySize      = req.CompanySize,
                    industry         = req.Industry,
                    contactName      = req.ContactName,
                    contactEmail     = req.ContactEmail,
                    contactPhone     = req.ContactPhone,
                    contactTitle     = req.ContactTitle,
                    partnershipTier  = req.PartnershipTier ?? "standard",
                    partnershipGoals = req.PartnershipGoals ?? Array.Empty<string>(),
                    budgetRange      = req.BudgetRange,
                    message          = req.Message,
                    howHeard         = req.HowHeard,
                });

            return Results.Created($"/api/partners/applications/{row.id}", new { success = true, id = row.id.ToString() });
        });

        // ── GET /api/partners/public ─────────────────────────────────────────
        // Lists approved partners (public, no auth)
        app.MapGet("/api/partners/public", async (
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            var partners = await conn.QueryAsync<dynamic>(
                """
                SELECT pa.id, pa.company_name, pa.company_website, pa.partnership_tier, pa.industry
                FROM partner_applications pa
                WHERE pa.status = 'approved'
                ORDER BY pa.partnership_tier DESC, pa.company_name
                """);

            return Results.Ok(partners);
        });
    }
}
