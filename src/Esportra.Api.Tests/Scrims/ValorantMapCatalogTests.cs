using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ValorantMapCatalogTests
{
    // Shaped like valorant-api.com/v1/maps. The codenames here are made up for the test, not real Riot codes.
    private const string Sample = """
        {"status":200,"data":[
          {"displayName":"Test Summit","mapUrl":"/Game/Maps/TestSummitCode/TestSummitCode","tacticalDescription":"A/B Sites"},
          {"displayName":"Haven Renamed","mapUrl":"/Game/Maps/Triad/Triad","tacticalDescription":"A/B/C Sites"},
          {"displayName":"Test Range","mapUrl":"/Game/Maps/Poveglia/Range","tacticalDescription":null},
          {"displayName":"Test Arena","mapUrl":"/Game/Maps/HURM/HURM_TestArena/HURM_TestArena","tacticalDescription":null},
          {"displayName":"","mapUrl":"/Game/Maps/Broken/Broken"},
          {"displayName":"No Path"}
        ]}
        """;

    [Fact]
    public void Parses_the_published_list_into_codes_names_and_whether_the_map_is_competitive_style()
    {
        var maps = ValorantMapCatalog.Parse(Sample).ToDictionary(m => m.Code);
        maps["TestSummitCode"].Should().Be(("TestSummitCode", "Test Summit", true));
        maps["Range"].Competitive.Should().BeFalse();
        maps["HURM_TestArena"].Competitive.Should().BeFalse();
        maps.Should().NotContainKey("Broken");
        maps.Count.Should().Be(4);
    }

    [Fact]
    public void A_response_that_is_not_a_map_list_teaches_nothing()
    {
        ValorantMapCatalog.Parse("""{"status":500}""").Should().BeEmpty();
        ValorantMapCatalog.Parse("""{"data":"nope"}""").Should().BeEmpty();
    }

    [Fact]
    public void A_learned_map_gets_its_name_and_joins_the_competitive_pool_but_never_overrides_a_built_in_name()
    {
        ValorantMaps.Learn(ValorantMapCatalog.Parse(Sample));
        ValorantMaps.DisplayName("/Game/Maps/TestSummitCode/TestSummitCode").Should().Be("Test Summit");
        ValorantMaps.Resolve("/Game/Maps/TestSummitCode/TestSummitCode").Should().Be("Test Summit");
        ValorantMaps.IsDisplayName("test summit").Should().BeTrue();
        ValorantMaps.CompetitiveNames.Should().Contain("Test Summit").And.NotContain("Test Range");
        ValorantMaps.DisplayName("/Game/Maps/Triad/Triad").Should().Be("Haven");
        ValorantMaps.CompetitiveNames.Should().Contain(["Haven", "Lotus"]);
    }

    [Fact]
    public void An_empty_list_keeps_what_was_learned_and_an_unknown_path_is_flagged_for_a_retry()
    {
        ValorantMaps.Learn(ValorantMapCatalog.Parse(Sample));
        ValorantMaps.Learn([]);
        ValorantMaps.DisplayName("/Game/Maps/TestSummitCode/TestSummitCode").Should().Be("Test Summit");
        ValorantMaps.Resolve("/Game/Maps/NeverHeardOf/NeverHeardOf").Should().Be("Unknown map");
        ValorantMaps.UnknownSeen.Should().BeTrue();
    }
}
