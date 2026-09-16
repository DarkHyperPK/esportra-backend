using Esportra.Api.Middleware;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.DeveloperApi;

public sealed class ApiKeyAuthMiddlewareTests
{
    // ── Key hash tests ────────────────────────────────────────────────────────

    [Fact]
    public void HashKey_ProducesDeterministicLowercaseHex()
    {
        var key = "ek_live_abcd1234efgh5678abcd1234efgh5678abcd1234efgh5678abcd1234efgh5678";
        var hash1 = ApiKeyAuthMiddleware.HashKey(key);
        var hash2 = ApiKeyAuthMiddleware.HashKey(key);

        hash1.Should().Be(hash2);
        hash1.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void HashKey_DifferentKeys_ProduceDifferentHashes()
    {
        var hash1 = ApiKeyAuthMiddleware.HashKey("ek_live_aaaa1234efgh5678abcd1234efgh5678abcd1234efgh5678abcd1234efgh5678");
        var hash2 = ApiKeyAuthMiddleware.HashKey("ek_live_bbbb1234efgh5678abcd1234efgh5678abcd1234efgh5678abcd1234efgh5678");

        hash1.Should().NotBe(hash2);
    }

    // ── Key generation tests ──────────────────────────────────────────────────

    [Theory]
    [InlineData("live", "ek_live_")]
    [InlineData("sandbox", "ek_sand_")]
    public void GenerateKey_PrefixMatchesEnvironment(string environment, string expectedPrefix)
    {
        var (rawKey, hash, prefix) = Esportra.Api.Endpoints.DeveloperKeyEndpoints.GenerateKey(environment);

        rawKey.Should().StartWith(expectedPrefix);
        prefix.Should().HaveLength(16);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
        rawKey.Should().HaveLength(expectedPrefix.Length + 64);
    }

    [Fact]
    public void GenerateKey_TwoCallsProduceDifferentKeys()
    {
        var (rawKey1, _, _) = Esportra.Api.Endpoints.DeveloperKeyEndpoints.GenerateKey("sandbox");
        var (rawKey2, _, _) = Esportra.Api.Endpoints.DeveloperKeyEndpoints.GenerateKey("sandbox");

        rawKey1.Should().NotBe(rawKey2);
    }

    [Fact]
    public void GenerateKey_HashMatchesKeyHash()
    {
        var (rawKey, hash, _) = Esportra.Api.Endpoints.DeveloperKeyEndpoints.GenerateKey("live");

        var expectedHash = ApiKeyAuthMiddleware.HashKey(rawKey);
        hash.Should().Be(expectedHash);
    }
}
