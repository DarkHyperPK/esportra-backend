using Esportra.Api.Helpers;
using Xunit;

namespace Esportra.Api.Tests;

public class ProfileFieldValidatorTests
{
    [Fact]
    public void TryValidateDateOfBirth_rejects_future_dates()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");
        var ok = ProfileFieldValidator.TryValidateDateOfBirth(future, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Date of birth cannot be in the future.", error);
    }

    [Fact]
    public void TryValidateDateOfBirth_accepts_valid_adult_dob()
    {
        var ok = ProfileFieldValidator.TryValidateDateOfBirth("1995-06-15", out var normalized, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("1995-06-15", normalized);
    }

    [Fact]
    public void TryValidateCountryCode_normalizes_to_uppercase()
    {
        var ok = ProfileFieldValidator.TryValidateCountryCode("pk", out var normalized, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("PK", normalized);
    }

    [Fact]
    public void TryValidateCountryCode_rejects_unknown_codes()
    {
        var ok = ProfileFieldValidator.TryValidateCountryCode("ZZ", out _, out var error);

        Assert.False(ok);
        Assert.Equal("Please select a valid country.", error);
    }
}
