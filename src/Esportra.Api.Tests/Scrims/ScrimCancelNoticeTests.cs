using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ScrimCancelNoticeTests
{
    private static readonly Guid T1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid T2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void Notifies_TheOtherTeam_WhenTeam1Cancels()
    {
        var notice = ScrimCancelNotice.For(T1, T2, cancelledByTeamId: T1, cancellerName: "Apex");
        notice.Should().NotBeNull();
        notice!.RecipientTeamId.Should().Be(T2);
        notice.Message.Should().Contain("Apex");
    }

    [Fact]
    public void Notifies_TheOtherTeam_WhenTeam2Cancels()
    {
        ScrimCancelNotice.For(T1, T2, T2, "Nova")!.RecipientTeamId.Should().Be(T1);
    }

    [Fact]
    public void NeverNotifiesTheCancellingTeam()
    {
        foreach (var canceller in new[] { T1, T2 })
            ScrimCancelNotice.For(T1, T2, canceller, "X")!.RecipientTeamId.Should().NotBe(canceller);
    }

    [Fact]
    public void ReturnsNull_WhenWhoCancelledIsUnknown_OrNotAParticipant()
    {
        ScrimCancelNotice.For(T1, T2, null, "X").Should().BeNull();
        ScrimCancelNotice.For(T1, T2, Guid.NewGuid(), "X").Should().BeNull();
    }

    [Fact]
    public void FallsBackToAGenericName_AndSaysWhatToDoNext()
    {
        var notice = ScrimCancelNotice.For(T1, T2, T1, "  ")!;
        notice.Message.Should().StartWith("The other team");
        notice.Title.Should().Be("Scrim cancelled");
        notice.Type.Should().Be("scrim_session_cancelled");
    }
}

public sealed class ScrimCancelNoticeContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Esportra.Infrastructure"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Migration_AddsTheEnumValue_Idempotently()
    {
        var path = Directory.GetFiles(
            Path.Combine(Root(), "src", "Esportra.Infrastructure", "Migrations", "Scripts"),
            "*_scrim_session_cancelled_notification.sql").Single();
        File.ReadAllText(path).Should().Contain(
            $"ADD VALUE IF NOT EXISTS '{ScrimCancelNotice.NotificationType}'");
    }

    [Fact]
    public void CancelEndpoint_NotifiesTheOpponent_AfterTheCancelSucceeds()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Esportra.Api", "Endpoints", "ScrimEndpoints.cs"));
        source.Should().Contain("await NotifyOpponentOfCancelAsync(conn, hub, id");
    }
}
