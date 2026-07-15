using System.Globalization;
using System.Text.Json;

namespace Esportra.Api.Helpers;

public static class ProfileFieldValidator
{
    private const int MinAgeYears = 13;
    private const int MaxAgeYears = 120;

    private static readonly HashSet<string> AllowedCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AF", "AL", "DZ", "AD", "AO", "AG", "AR", "AM", "AU", "AT", "AZ", "BS", "BH", "BD", "BB", "BY", "BE", "BZ",
        "BJ", "BT", "BO", "BA", "BW", "BR", "BN", "BG", "BF", "BI", "CV", "KH", "CM", "CA", "CF", "TD", "CL", "CN",
        "CO", "KM", "CG", "CR", "HR", "CU", "CY", "CZ", "DK", "DJ", "DM", "DO", "EC", "EG", "SV", "GQ", "ER", "EE",
        "SZ", "ET", "FJ", "FI", "FR", "GA", "GM", "GE", "DE", "GH", "GR", "GD", "GT", "GN", "GW", "GY", "HT", "VA",
        "HN", "HU", "IS", "IN", "ID", "IR", "IQ", "IE", "IL", "IT", "JM", "JP", "JO", "KZ", "KE", "KI", "KW", "KG",
        "LA", "LV", "LB", "LS", "LR", "LY", "LI", "LT", "LU", "MG", "MW", "MY", "MV", "ML", "MT", "MH", "MR", "MU",
        "MX", "FM", "MD", "MC", "MN", "ME", "MA", "MZ", "MM", "NA", "NR", "NP", "NL", "NZ", "NI", "NE", "NG", "KP",
        "MK", "NO", "OM", "PK", "PW", "PS", "PA", "PG", "PY", "PE", "PH", "PL", "PT", "QA", "RO", "RU", "RW", "KN",
        "LC", "VC", "WS", "SM", "ST", "SA", "SN", "RS", "SC", "SL", "SG", "SK", "SI", "SB", "SO", "ZA", "KR", "SS",
        "ES", "LK", "SD", "SR", "SE", "CH", "SY", "TJ", "TZ", "TH", "TL", "TG", "TO", "TT", "TN", "TR", "TM", "TV",
        "UG", "UA", "AE", "GB", "US", "UY", "UZ", "VU", "VE", "VN", "YE", "ZM", "ZW",
    };

    public static bool TryValidateDateOfBirth(object? value, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        var raw = ExtractString(value);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Date of birth is required.";
            return false;
        }

        if (!DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
        {
            error = "Enter a valid date (YYYY-MM-DD).";
            return false;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (dob > today)
        {
            error = "Date of birth cannot be in the future.";
            return false;
        }

        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age))
            age--;

        if (age < MinAgeYears)
        {
            error = "You must be at least 13 years old.";
            return false;
        }

        if (age > MaxAgeYears)
        {
            error = "Please enter a valid date of birth.";
            return false;
        }

        normalized = dob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryValidateCountryCode(object? value, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        var raw = ExtractString(value);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Country is required.";
            return false;
        }

        normalized = raw.Trim().ToUpperInvariant();
        if (!AllowedCountryCodes.Contains(normalized))
        {
            error = "Please select a valid country.";
            normalized = null;
            return false;
        }

        return true;
    }

    private static string? ExtractString(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            _ => value.ToString(),
        };
    }
}
