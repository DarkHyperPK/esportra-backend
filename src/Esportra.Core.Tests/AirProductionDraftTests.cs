using System.Text.Json;
using Esportra.Core.Air;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests;

public sealed class AirProductionDraftTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sponsors\":[{\"name\":\"Acme\",\"logoUrl\":\"https://assets.example.com/logo.png\"}],\"talent\":[{\"name\":\"Host\",\"role\":\"Commentator\"}]}")]
    [InlineData("{\"sponsors\":[{\"name\":\"Acme\",\"assetId\":\"00000000-0000-4000-8000-000000000001\"}]}")]
    public void Broadcast_only_details_are_valid(string json) => AirProductionDraft.Validate(JsonSerializer.Deserialize<JsonElement>(json)).Should().BeNull();

    [Theory]
    [InlineData("{\"officialScore\":[]}")]
    [InlineData("{\"talent\":[{\"name\":\"<script>\"}]}")]
    [InlineData("{\"sponsors\":[{\"name\":\"Acme\",\"logoUrl\":\"https://user:secret@example.com/logo.png\"}]}")]
    [InlineData("{\"sponsors\":[{\"name\":\"Acme\",\"logoUrl\":\"https://example.com/logo.png?token=private\"}]}")]
    [InlineData("{\"sponsors\":[{\"name\":\"Acme\",\"assetId\":\"../other-account\"}]}")]
    [InlineData("{\"crew\":[{\"name\":\"\"}]}")]
    public void Identity_mutations_secrets_and_invalid_assets_are_rejected(string json) => AirProductionDraft.Validate(JsonSerializer.Deserialize<JsonElement>(json)).Should().NotBeNull();
}
