using Esportra.Api.Services.ScrimInsights;
using Esportra.Api.Services.ScrimReports;
using Esportra.Api.Tests.ScrimInsights;

namespace Esportra.Api.Tests.ScrimReports;

/// <summary>Maps built from the hand-checkable Sample match (a1 = Nova on 200 ACS, a2 = Ray on 100), each with its own result, map and date.</summary>
internal static class ReportFixture
{
    public static readonly Guid Nova = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Ray = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    public static ReportMatch Map(string map, string result, int rounds, int against, int day, string opponent = "Omega")
    {
        var match = InsightsFixture.Sample();
        var game = new ReportGame(Guid.NewGuid(), new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(day), map, Guid.NewGuid(), opponent, result, rounds, against);
        var users = new Dictionary<string, RosterUser> { ["a1"] = new(Nova, "Nova"), ["a2"] = new(Ray, "Ray") };
        return new ReportMatch(game, match, ScrimInsightsBuilder.Build(match), users);
    }

    /// <summary>Three Lotus wins then three Bind losses.</summary>
    public static List<ReportMatch> Six() =>
    [
        Map("Lotus", "win", 13, 7, 1), Map("Lotus", "win", 13, 5, 2), Map("Lotus", "win", 13, 9, 3),
        Map("Bind", "loss", 6, 13, 4), Map("Bind", "loss", 9, 13, 5), Map("Bind", "loss", 4, 13, 6),
    ];
}
