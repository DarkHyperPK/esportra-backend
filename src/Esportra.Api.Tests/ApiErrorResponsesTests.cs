using Esportra.Api.Helpers;
using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class ApiErrorResponsesTests
{
    [Fact]
    public void FromException_maps_game_catalog_validation_to_bad_request()
    {
        var payload = ApiErrorResponses.FromException(
            new GameCatalogValidationException("Roster size does not match tournament team size."),
            "/api/tournaments/abc/register",
            includeDiagnostics: false);

        Assert.Equal(400, payload.StatusCode);
        Assert.Equal("Roster size does not match tournament team size.", payload.Error);
    }

    [Fact]
    public void FromException_uses_request_path_context_for_unknown_errors()
    {
        var payload = ApiErrorResponses.FromException(
            new Exception("internal failure"),
            "/api/invitations/redeem",
            includeDiagnostics: false);

        Assert.Equal(500, payload.StatusCode);
        Assert.Contains("invite redemption", payload.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(payload.Detail);
    }

    [Fact]
    public void FromException_includes_detail_on_staging()
    {
        var payload = ApiErrorResponses.FromException(
            new InvalidOperationException("boom"),
            "/api/invitations/redeem",
            includeDiagnostics: true);

        Assert.Equal("boom", payload.Error);
        Assert.Null(payload.Detail);
    }
}
