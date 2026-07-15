using Esportra.Api.Helpers;
using Xunit;

namespace Esportra.Api.Tests;

public class ProfileResponseNormalizerTests
{
    [Fact]
    public void NormalizeValue_serializes_date_only_as_iso_string()
    {
        var normalized = ProfileResponseNormalizer.NormalizeValue(new DateOnly(1995, 6, 15));

        Assert.Equal("1995-06-15", normalized);
    }

    [Fact]
    public void ToDictionary_normalizes_date_of_birth_values()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = Guid.Parse("e9f59c99-5e09-4256-8efd-088826fdf5f9"),
            ["date_of_birth"] = new DateOnly(1995, 6, 15),
            ["country_code"] = "PK",
        };

        var normalized = ProfileResponseNormalizer.ToDictionary(row);

        Assert.NotNull(normalized);
        Assert.Equal("1995-06-15", normalized["date_of_birth"]);
        Assert.Equal("PK", normalized["country_code"]);
    }
}
