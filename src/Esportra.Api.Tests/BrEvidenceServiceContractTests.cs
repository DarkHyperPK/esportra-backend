using Esportra.Core.Br;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class BrEvidenceServiceContractTests
{
    [Fact]
    public void MapRow_formats_evidence_entry()
    {
        dynamic row = new System.Dynamic.ExpandoObject();
        row.entity_id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        row.entity_name = "Team Alpha";
        row.logo_url = null;
        row.image_url = "https://example.com/evidence.png";
        row.submitted_at = DateTime.UtcNow;
        row.placement = 2;
        row.kills = 5;
        row.reviewed = false;

        var entry = BrEvidenceRepository.MapRow(row);

        Assert.Equal("Team Alpha", entry.TeamName);
        Assert.Equal(2, entry.Placement);
        Assert.False(entry.Reviewed);
    }
}
