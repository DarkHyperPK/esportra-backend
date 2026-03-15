using System.Text.Json;

namespace Esportra.Core;

/// <summary>
/// Shared JSON serialization options — snake_case for consistency with
/// PostgreSQL column names, Dapper dynamic results, and frontend expectations.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}
