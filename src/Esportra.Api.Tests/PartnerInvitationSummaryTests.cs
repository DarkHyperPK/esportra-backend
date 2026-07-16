using System.Text.Json;
using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class PartnerInvitationSummaryTests
{
    [Fact]
    public void Summary_SerializesUtcTimestampsAsCamelCaseJson()
    {
        var createdAt = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        var summary = new PartnerInvitationSummary
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SponsorId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SponsorName = "Partner",
            Email = "partner@example.com",
            Role = "owner",
            Status = "pending",
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddHours(24),
        };

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"sponsorName\":\"Partner\"", json);
        Assert.Contains("\"createdAt\":\"2026-07-17T12:00:00Z\"", json);
        Assert.Contains("\"deliveredAt\":null", json);
        Assert.Contains("\"acceptedAt\":null", json);
    }

    [Fact]
    public void Summary_PreservesOptionalUtcTimestamps()
    {
        var timestamp = new DateTime(2026, 7, 17, 13, 30, 0, DateTimeKind.Utc);
        var summary = new PartnerInvitationSummary
        {
            DeliveredAt = timestamp,
            AcceptedAt = timestamp.AddMinutes(5),
        };

        Assert.Equal(DateTimeKind.Utc, summary.DeliveredAt?.Kind);
        Assert.Equal(DateTimeKind.Utc, summary.AcceptedAt?.Kind);
    }
}
