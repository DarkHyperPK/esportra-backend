using System.Text.Json;
using Esportra.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

/// <summary>The client sends evidence_urls; the endpoint used to read only responder_evidence_urls and stored nothing.</summary>
public class ScrimEvidenceRequestTests
{
    [Fact]
    public void Deserializes_EvidenceUrls_FromTheClientBody()
    {
        var req = JsonSerializer.Deserialize<SubmitScrimEvidenceRequest>("{\"evidence_urls\":[\"https://a.test/1.png\"],\"game_number\":2}")!;
        req.EvidenceUrls.Should().Equal("https://a.test/1.png");
        req.GameNumber.Should().Be(2);
    }

    [Fact]
    public void Deserializes_TheOlderResponderName()
    {
        var req = JsonSerializer.Deserialize<SubmitScrimEvidenceRequest>("{\"responder_evidence_urls\":[\"https://a.test/2.png\"]}")!;
        req.ResponderEvidenceUrls.Should().Equal("https://a.test/2.png");
        req.EvidenceUrls.Should().BeNull();
    }
}
