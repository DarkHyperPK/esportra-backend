namespace Esportra.Contracts.Requests;

public sealed record PartnerApplicationRequest(
    string CompanyName,
    string CompanyWebsite,
    string CompanySize,
    string Industry,
    string ContactName,
    string ContactEmail,
    string? ContactPhone = null,
    string? ContactTitle = null,
    string? PartnershipTier = "standard",
    string[]? PartnershipGoals = null,
    string? BudgetRange = null,
    string? Message = null,
    string? HowHeard = null);
