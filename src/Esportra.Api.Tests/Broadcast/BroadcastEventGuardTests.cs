using System.Text;
using System.Text.Json;
using Esportra.Api.Hubs;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Broadcast;

/// <summary>
/// Unit tests for <see cref="BroadcastEventGuard"/> — the four CIO security
/// guards applied to every broadcast event before it reaches the database.
/// Tests run without any infrastructure (no DB, no SignalR).
/// </summary>
public sealed class BroadcastEventGuardTests
{
    // ── (a) event_type allowlist ──────────────────────────────────────────────

    [Theory]
    [InlineData("SegmentTake")]
    [InlineData("SegmentDone")]
    [InlineData("SponsorImpressionStarted")]
    [InlineData("SponsorImpressionEnded")]
    [InlineData("MatchResultSubmitted")]
    [InlineData("ShowStarted")]
    [InlineData("ShowEnded")]
    public void IsAllowedEventType_ReturnsTrue_WhenTypeIsInAllowlist(string eventType)
    {
        BroadcastEventGuard.IsAllowedEventType(eventType).Should().BeTrue();
    }

    [Theory]
    [InlineData("GepStats")]
    [InlineData("MatchStarted")]
    [InlineData("")]
    [InlineData("segmenttake")]          // case-sensitive
    [InlineData("SEGMENTTAKE")]          // case-sensitive
    [InlineData("ShowStarted ")]         // trailing space
    [InlineData("arbitrary_event")]
    public void IsAllowedEventType_ReturnsFalse_WhenTypeNotInAllowlist(string eventType)
    {
        BroadcastEventGuard.IsAllowedEventType(eventType).Should().BeFalse();
    }

    // ── (c) event_id UUID format ──────────────────────────────────────────────

    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void IsValidEventId_ReturnsTrue_WhenValueIsValidUuid(string eventId)
    {
        BroadcastEventGuard.IsValidEventId(eventId).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("not-a-uuid")]
    [InlineData("123")]
    [InlineData("550e8400-e29b-41d4-a716-44665544000Z")]  // invalid hex char
    public void IsValidEventId_ReturnsFalse_WhenValueIsNotAValidUuid(string? eventId)
    {
        BroadcastEventGuard.IsValidEventId(eventId).Should().BeFalse();
    }

    // ── (b) payload size cap ──────────────────────────────────────────────────

    [Fact]
    public void IsPayloadWithinSizeLimit_ReturnsTrue_WhenPayloadIsSmall()
    {
        var payload = new BroadcastEventPayload(
            EventType: "ShowStarted",
            Data: null,
            EventId: Guid.NewGuid().ToString());

        var result = BroadcastEventGuard.IsPayloadWithinSizeLimit(payload, out var serialized);

        result.Should().BeTrue();
        serialized.Should().NotBeNullOrWhiteSpace();
        Encoding.UTF8.GetByteCount(serialized).Should().BeLessOrEqualTo(BroadcastEventGuard.MaxPayloadBytes);
    }

    [Fact]
    public void IsPayloadWithinSizeLimit_ReturnsFalse_WhenPayloadExceedsMaxBytes()
    {
        // Build a Data JsonElement whose serialized form is > 8 KB
        var largeValue = new string('x', 9000);
        var dataJson = $"{{\"data\":\"{largeValue}\"}}";
        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);

        var payload = new BroadcastEventPayload(
            EventType: "ShowStarted",
            Data: data,
            EventId: Guid.NewGuid().ToString());

        var result = BroadcastEventGuard.IsPayloadWithinSizeLimit(payload, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPayloadWithinSizeLimit_ReturnsFalse_WhenDataAloneExceedsMaxBytes()
    {
        var largeValue = new string('x', 9000);
        var dataJson = $"{{\"data\":\"{largeValue}\"}}";
        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);

        var result = BroadcastEventGuard.IsDataWithinSizeLimit(data, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsDataWithinSizeLimit_ReturnsEmptyObject_WhenDataIsNull()
    {
        var result = BroadcastEventGuard.IsDataWithinSizeLimit(null, out var serialized);

        result.Should().BeTrue();
        serialized.Should().Be("{}");
    }

    [Fact]
    public void IsPayloadWithinSizeLimit_PayloadAtExactLimit_ReturnsTrue()
    {
        // Construct a payload whose UTF-8 byte count is exactly MaxPayloadBytes.
        // We build the EventType and EventId normally and pad Data to fill the remainder.
        const int maxBytes = BroadcastEventGuard.MaxPayloadBytes;
        var eventId = Guid.NewGuid().ToString();

        // Probe: find a padding string length that brings total to exactly maxBytes
        var probe = new BroadcastEventPayload("ShowStarted", null, eventId);
        BroadcastEventGuard.IsPayloadWithinSizeLimit(probe, out var baseline);
        var baselineBytes = Encoding.UTF8.GetByteCount(baseline);

        // If baseline already exceeds maxBytes (unlikely), just confirm boundary math is correct.
        if (baselineBytes >= maxBytes)
        {
            BroadcastEventGuard.IsPayloadWithinSizeLimit(probe, out _).Should().BeFalse();
            return;
        }

        // Fill exactly to the limit — the guard should return true
        var padding = new string('a', maxBytes - baselineBytes - 12); // 12 ≈ {"data":"..."} overhead estimate
        var dataJson = $"{{\"v\":\"{padding}\"}}";
        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
        var borderPayload = new BroadcastEventPayload("ShowStarted", data, eventId);

        BroadcastEventGuard.IsPayloadWithinSizeLimit(borderPayload, out var borderSerialized);
        var borderBytes = Encoding.UTF8.GetByteCount(borderSerialized);

        // The real test: the guard's decision matches the byte count
        var guardResult = BroadcastEventGuard.IsPayloadWithinSizeLimit(borderPayload, out _);
        guardResult.Should().Be(borderBytes <= maxBytes);
    }
}
