using Esportra.Core.Match;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Veto;

public sealed class VetoEngineTests
{
    private static readonly Guid Team1 = Guid.NewGuid();
    private static readonly Guid Team2 = Guid.NewGuid();

    // ── Actor permission ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateTransition_ReturnsNotYourTurn_WhenCaptainActsOutOfTurn()
    {
        var context = new TurnContext(
            CurrentTeamId: Team1,
            IsOrganizer: false,
            UserTeamId: Team2.ToString(),
            IsCaptain: true);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.BanMap, context);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("NOT_YOUR_TURN");
    }

    [Fact]
    public void ValidateTransition_ReturnsForbidden_WhenNeitherCaptainNorOrganizer()
    {
        var context = new TurnContext(
            CurrentTeamId: Team1,
            IsOrganizer: false,
            UserTeamId: Team1.ToString(),
            IsCaptain: false);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.BanMap, context);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("FORBIDDEN");
    }

    [Fact]
    public void ValidateTransition_OrganizerCanActOnBehalfOfEitherTeam()
    {
        var context = new TurnContext(
            CurrentTeamId: Team1,
            IsOrganizer: true,
            UserTeamId: null,
            IsCaptain: false);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.BanMap, context);

        result.Ok.Should().BeTrue();
    }

    // ── State machine ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidateTransition_ReturnsInvalidState_WhenBanningDuringPickPhase()
    {
        var context = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Pick, VetoEvent.BanMap, context);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("INVALID_STATE");
    }

    [Fact]
    public void ValidateTransition_ReturnsInvalidState_WhenPickingDuringBanPhase()
    {
        var context = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.PickMap, context);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("INVALID_STATE");
    }

    [Fact]
    public void ValidateTransition_ReturnsInvalidState_WhenVetoIsComplete()
    {
        var context = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Complete, VetoEvent.BanMap, context);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("INVALID_STATE");
    }

    [Fact]
    public void ValidateTransition_SetBo_RequiresOrganizer()
    {
        var captainContext = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Init, VetoEvent.SetBo, captainContext);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("FORBIDDEN");
    }

    [Fact]
    public void ValidateTransition_SetBo_FailsWhenNotInInitState()
    {
        var orgContext = new TurnContext(Team1, true, null, false);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.SetBo, orgContext);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("INVALID_STATE");
    }

    // ── Map reuse guard ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateTransition_RejectsAlreadyBannedMap()
    {
        const string mapId = "de_inferno";
        var veto = new MatchMapVeto
        {
            Team1BannedMaps = [mapId],
            Team2BannedMaps = [],
            Team1PickedMaps = [],
            Team2PickedMaps = [],
            Status = "in_progress",
            CurrentAction = "ban",
        };
        var context = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.BanMap, context, mapId, veto);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("MAP_ALREADY_USED");
    }

    [Fact]
    public void ValidateTransition_RejectsAlreadyPickedMapOnBan()
    {
        const string mapId = "de_dust2";
        var veto = new MatchMapVeto
        {
            Team1BannedMaps = [],
            Team2BannedMaps = [],
            Team1PickedMaps = [new PickedMap(mapId)],
            Team2PickedMaps = [],
            Status = "in_progress",
            CurrentAction = "ban",
        };
        var context = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.BanMap, context, mapId, veto);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("MAP_ALREADY_USED");
    }

    [Fact]
    public void ValidateTransition_AllowsPickSideOnAlreadyPickedMap()
    {
        const string mapId = "de_mirage";
        var veto = new MatchMapVeto
        {
            Team1BannedMaps = [],
            Team2BannedMaps = [],
            Team1PickedMaps = [new PickedMap(mapId)],
            Team2PickedMaps = [],
            Status = "in_progress",
            CurrentAction = "pick_side",
        };
        var context = new TurnContext(Team2, false, Team2.ToString(), true);

        // Picking a side on an already-picked map is valid
        var result = VetoEngine.ValidateTransition(VetoState.PickSide, VetoEvent.PickSide, context, mapId, veto);

        result.Ok.Should().BeTrue();
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateTransition_Reset_AllowedForOrganizer_InAnyState()
    {
        var orgContext = new TurnContext(Team1, true, null, false);

        foreach (var state in Enum.GetValues<VetoState>())
        {
            var result = VetoEngine.ValidateTransition(state, VetoEvent.Reset, orgContext);
            result.Ok.Should().BeTrue($"organizer should be able to reset from state {state}");
        }
    }

    [Fact]
    public void ValidateTransition_Reset_ForbiddenForCaptain()
    {
        var captainContext = new TurnContext(Team1, false, Team1.ToString(), true);

        var result = VetoEngine.ValidateTransition(VetoState.Ban, VetoEvent.Reset, captainContext);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("FORBIDDEN");
    }

    // ── State derivation ──────────────────────────────────────────────────────

    [Fact]
    public void DeriveState_NullVeto_ReturnsInit() =>
        VetoEngine.DeriveState(null).Should().Be(VetoState.Init);

    [Fact]
    public void DeriveState_CompletedVeto_ReturnsComplete() =>
        VetoEngine.DeriveState(new MatchMapVeto { Status = "completed" }).Should().Be(VetoState.Complete);

    [Theory]
    [InlineData("ban", VetoState.Ban)]
    [InlineData("pick", VetoState.Pick)]
    [InlineData("pick_side", VetoState.PickSide)]
    public void DeriveState_MapsCurrentActionToCorrectState(string action, VetoState expected)
    {
        var veto = new MatchMapVeto
        {
            Status = "in_progress",
            CurrentAction = action,
            CurrentTeamId = Team1,
        };

        VetoEngine.DeriveState(veto).Should().Be(expected);
    }
}
