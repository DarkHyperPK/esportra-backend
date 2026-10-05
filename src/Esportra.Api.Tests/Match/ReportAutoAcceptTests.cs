using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.Match;

/// <summary>The opponent's response window: a report nobody answers is accepted when it runs out.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class ReportAutoAcceptTests(ApiFactory factory)
{
    private sealed record Seeded(Guid MatchId, Guid Team1Id, Guid Team2Id, Guid Captain1, Guid Captain2, Guid Organizer);

    private async Task<Seeded> SeedMatchAsync()
    {
        var organizer = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(organizer);
        await factory.Seeder.SeedUserRoleAsync(organizer, "organizer");
        var tournamentId = await factory.Seeder.SeedTournamentAsync(organizer);
        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
            await conn.ExecuteAsync("UPDATE tournaments SET game = 'Valorant' WHERE id = @tournamentId", new { tournamentId });
        var captain1 = Guid.NewGuid();
        var captain2 = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(captain1);
        await factory.Seeder.SeedAuthUserAsync(captain2);
        var team1 = await factory.Seeder.SeedTeamAsync(captain1, "Alpha");
        var team2 = await factory.Seeder.SeedTeamAsync(captain2, "Bravo");
        var stageId = await factory.Seeder.SeedStageAsync(tournamentId);
        var versionId = await factory.Seeder.SeedBracketVersionAsync(tournamentId, stageId, "active");
        var matchId = await factory.Seeder.SeedMatchAsync(versionId, team1Id: team1, team2Id: team2, matchStatus: "in_progress");
        return new Seeded(matchId, team1, team2, captain1, captain2, organizer);
    }

    private async Task<Guid> SubmitAsync(Seeded seeded, Guid captain, Guid team, int team1Score, int team2Score)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(captain));
        var response = await client.PostAsJsonAsync($"/api/matches/{seeded.MatchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = team.ToString(),
            team1Score,
            team2Score,
            matchData = new { players = Array.Empty<object>() },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private async Task ExpireAsync(Guid reportId)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.ExecuteAsync("UPDATE match_result_reports SET accept_by = now() - interval '1 minute' WHERE id = @reportId", new { reportId });
    }

    private async Task RunJobAsync(Guid reportId)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ReportAutoAcceptJob>().RunAsync(reportId, CancellationToken.None);
    }

    private async Task<(string Status, string? AutoAccepted)> StateAsync(Guid reportId)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        return await conn.QuerySingleAsync<(string, string?)>(
            "SELECT status, auto_accepted FROM match_result_reports WHERE id = @reportId", new { reportId });
    }

    [Fact]
    public async Task Submit_GivesTheOpponentFifteenMinutes()
    {
        var seeded = await SeedMatchAsync();
        var before = DateTime.UtcNow;

        var reportId = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);

        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        var acceptBy = await conn.QuerySingleAsync<DateTime>("SELECT accept_by FROM match_result_reports WHERE id = @reportId", new { reportId });
        acceptBy.Should().BeCloseTo(before.AddMinutes(15), TimeSpan.FromMinutes(1));
        (await conn.QuerySingleAsync<string>(
            "SELECT message FROM notifications WHERE user_id = @captain2 AND type = 'result_reported' ORDER BY created_at DESC LIMIT 1",
            new { captain2 = seeded.Captain2 })).Should().Contain("within 15 minutes");
    }

    [Fact]
    public async Task BeforeTheDeadline_TheReportWaits()
    {
        var seeded = await SeedMatchAsync();
        var reportId = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);

        await RunJobAsync(reportId);

        (await StateAsync(reportId)).Should().Be(("pending", (string?)null));
    }

    [Fact]
    public async Task AfterTheDeadline_TheReportIsAcceptedAndTheGameRecorded()
    {
        var seeded = await SeedMatchAsync();
        var reportId = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);
        await ExpireAsync(reportId);

        await RunJobAsync(reportId);

        (await StateAsync(reportId)).Should().Be(("accepted", "timeout"));
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        (await conn.QuerySingleAsync<Guid?>(
            "SELECT winner_id FROM brkt_match_games WHERE match_id = @MatchId AND status = 'completed'", seeded))
            .Should().Be(seeded.Team1Id);
        (await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*)::int FROM notifications WHERE user_id = ANY(@users) AND type = 'result_accepted' AND data->>'auto_accepted' = 'timeout'",
            new { users = new[] { seeded.Captain1, seeded.Captain2 } })).Should().Be(2);
    }

    [Fact]
    public async Task ADisputeInTime_IsNeverOverridden()
    {
        var seeded = await SeedMatchAsync();
        var reportId = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);
        await ExpireAsync(reportId);
        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
            await conn.ExecuteAsync("UPDATE match_result_reports SET status = 'disputed' WHERE id = @reportId", new { reportId });

        await RunJobAsync(reportId);

        (await StateAsync(reportId)).Status.Should().Be("disputed");
    }

    [Fact]
    public async Task TwoDifferentResults_AreAConflict_NotAccepted()
    {
        var seeded = await SeedMatchAsync();
        var mine = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);
        var theirs = await SubmitAsync(seeded, seeded.Captain2, seeded.Team2Id, 5, 13);
        await ExpireAsync(mine);

        await RunJobAsync(mine);

        (await StateAsync(mine)).Status.Should().Be("pending");
        (await StateAsync(theirs)).Status.Should().Be("pending");
    }

    [Fact]
    public async Task TwoMatchingResults_AreBothSettled()
    {
        var seeded = await SeedMatchAsync();
        var mine = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);
        var theirs = await SubmitAsync(seeded, seeded.Captain2, seeded.Team2Id, 13, 5);
        await ExpireAsync(mine);

        await RunJobAsync(mine);

        (await StateAsync(mine)).Should().Be(("accepted", "timeout"));
        (await StateAsync(theirs)).Status.Should().Be("accepted");
    }

    [Fact]
    public async Task AReportWithWarnings_IsStillAccepted_AndOrganizersAreTold()
    {
        var seeded = await SeedMatchAsync();
        var reportId = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);
        var flagged = new OcrVerification(
            [new VerifiedPlayer(0, "Ringer", "team1", null, VerificationStatus.Unknown, null, "Ringer")], []);
        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
            await conn.ExecuteAsync(
                "UPDATE match_result_reports SET ocr_verification = @json::jsonb WHERE id = @reportId",
                new { reportId, json = JsonSerializer.Serialize(flagged, OcrVerificationJson.Options) });
        await ExpireAsync(reportId);

        await RunJobAsync(reportId);

        (await StateAsync(reportId)).Should().Be(("accepted", "timeout"));
        await using var check = new NpgsqlConnection(factory.ConnectionString);
        (await check.QuerySingleAsync<string>(
            "SELECT message FROM notifications WHERE user_id = @Organizer AND type = 'match_report_flag'", seeded))
            .Should().Contain("Ringer isn't on either team's roster").And.Contain("accepted automatically");
    }

    [Fact]
    public async Task Sweep_AcceptsAnOverdueReportWhoseScheduledJobWasLost()
    {
        var seeded = await SeedMatchAsync();
        var reportId = await SubmitAsync(seeded, seeded.Captain1, seeded.Team1Id, 13, 5);
        await ExpireAsync(reportId);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ReportAutoAcceptJob>().SweepAsync(CancellationToken.None);

        (await StateAsync(reportId)).Status.Should().Be("accepted");
    }
}
