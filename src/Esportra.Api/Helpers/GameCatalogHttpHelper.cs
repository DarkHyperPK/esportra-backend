namespace Esportra.Api.Helpers;

public static class GameCatalogHttpHelper
{
    public static bool MatchesETag(string? ifNoneMatchHeader, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatchHeader))
            return false;

        var quoted = $"\"{contentHash}\"";
        foreach (var token in ifNoneMatchHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "*") return true;
            if (string.Equals(token, quoted, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
