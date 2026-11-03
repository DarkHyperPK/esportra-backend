using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ValorantMapsTests
{
    [Theory]
    [InlineData("/Game/Maps/Triad/Triad", "Haven")]
    [InlineData("Triad", "Haven")]
    [InlineData("duality", "Bind")]
    [InlineData("/Game/Maps/Rook/Rook", "Corrode")]
    [InlineData("/Game/Maps/Infinity/Infinity", "Abyss")]
    [InlineData("/Game/Maps/HURM/HURM_Alley/HURM_Alley", "District")]
    public void Riot_codes_and_paths_resolve_to_the_name_players_use(string id, string expected)
    {
        ValorantMaps.DisplayName(id).Should().Be(expected);
        ValorantMaps.Resolve(id).Should().Be(expected);
    }

    [Fact]
    public void An_unrecognised_riot_path_is_never_shown_as_its_code()
    {
        ValorantMaps.DisplayName("/Game/Maps/Mystery/Mystery").Should().BeNull();
        ValorantMaps.Resolve("/Game/Maps/Mystery/Mystery").Should().Be("Unknown map");
    }

    [Fact]
    public void Typed_names_pass_through_and_empty_stays_empty()
    {
        ValorantMaps.Resolve("Haven").Should().Be("Haven");
        ValorantMaps.Resolve("Skirmish A").Should().Be("Skirmish A");
        ValorantMaps.Resolve(null).Should().BeEmpty();
    }

    [Fact]
    public void Every_competitive_map_including_corrode_is_in_the_pool_list()
    {
        ValorantMaps.CompetitiveNames.Should().Contain(["Ascent", "Bind", "Haven", "Split", "Icebox", "Breeze", "Fracture", "Pearl", "Lotus", "Sunset", "Abyss", "Corrode"]);
        ValorantMaps.CompetitiveNames.Should().NotContain(["District", "Kasbah", "Piazza"]);
    }
}
