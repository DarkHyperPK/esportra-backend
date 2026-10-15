using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Infrastructure.Email;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    private async Task<(Guid Owner, Guid Show, Guid Recipient, string Email)> SeedCrewDeliveryAsync(bool verified = true)
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var show = await SeedShowAsync(org, owner);
        var recipient = Guid.NewGuid();
        var email = $"crew-{recipient:N}@test.esportra.com";
        await _seeder.SeedAuthUserAsync(recipient, email);
        if (verified) { await using var conn = _seeder.OpenConnection(); await conn.ExecuteAsync("UPDATE auth.users SET email_confirmed_at = NOW() WHERE id = @recipient", new { recipient }); }
        return (owner, show, recipient, email);
    }

    [Fact]
    public async Task CrewDelivery_SubmitsEmailAndPrivateInbox_AndOnlyVerifiedRecipientCanAccept()
    {
        var setup = await SeedCrewDeliveryAsync();
        var owner = _factory.CreateAuthenticatedClient(setup.Owner);
        var created = await owner.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/crew/invites", new { role = "observer", email = setup.Email });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var invitation = await created.Content.ReadFromJsonAsync<JsonElement>();
        invitation.GetProperty("emailStatus").GetString().Should().Be("submitted");
        _factory.EmailSink.Messages.Should().Contain(message => message.Recipient == setup.Email && message.Type == EmailType.BroadcastCrewInvite);
        var recipient = _factory.CreateAuthenticatedClient(setup.Recipient);
        var inbox = await recipient.GetFromJsonAsync<JsonElement>("/api/broadcast/crew/invitations");
        inbox.GetArrayLength().Should().Be(1);
        inbox[0].TryGetProperty("token", out _).Should().BeFalse();
        inbox[0].TryGetProperty("recipientEmail", out _).Should().BeFalse();
        var id = invitation.GetProperty("inviteId").GetGuid();
        (await owner.PostAsJsonAsync($"/api/broadcast/crew/invitations/{id}/accept", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PostAsJsonAsync("/api/broadcast/crew/invites/accept", new { token = invitation.GetProperty("token").GetString() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await recipient.PostAsJsonAsync($"/api/broadcast/crew/invitations/{id}/accept", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await recipient.PostAsJsonAsync($"/api/broadcast/crew/invitations/{id}/accept", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var conn = _seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM notifications WHERE user_id = @recipient AND link = '/broadcast/invitations'", new { recipient = setup.Recipient })).Should().Be(1);
    }

    [Fact]
    public async Task CrewDelivery_EmailFailurePreservesInvitation_AndUnverifiedAccountCannotClaimIt()
    {
        var setup = await SeedCrewDeliveryAsync(verified: false);
        _factory.EmailSink.Fail = true;
        JsonElement invitation;
        try
        {
            var response = await _factory.CreateAuthenticatedClient(setup.Owner).PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/crew/invites", new { role = "monitor", email = setup.Email });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            invitation = await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        finally { _factory.EmailSink.Fail = false; }
        invitation.GetProperty("emailStatus").GetString().Should().Be("failed");
        var client = _factory.CreateAuthenticatedClient(setup.Recipient);
        (await client.GetFromJsonAsync<JsonElement>("/api/broadcast/crew/invitations")).GetArrayLength().Should().Be(0);
        var id = invitation.GetProperty("inviteId").GetGuid();
        (await client.PostAsJsonAsync($"/api/broadcast/crew/invitations/{id}/accept", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE auth.users SET email_confirmed_at = NOW() WHERE id = @recipient", new { recipient = setup.Recipient });
        (await client.PostAsJsonAsync($"/api/broadcast/crew/invitations/{id}/accept", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CrewDelivery_RevokeRemovesInboxEntry_AndInvalidEmailCreatesNothing()
    {
        var setup = await SeedCrewDeliveryAsync();
        var client = _factory.CreateAuthenticatedClient(setup.Owner);
        var route = $"/api/broadcast/shows/{setup.Show}/crew/invites";
        (await client.PostAsJsonAsync(route, new { role = "observer", email = "display <bad@example.com>" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var issued = await client.PostAsJsonAsync(route, new { role = "observer", email = setup.Email });
        var id = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteId").GetGuid();
        (await client.DeleteAsync(route + "/" + id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.CreateAuthenticatedClient(setup.Recipient).GetFromJsonAsync<JsonElement>("/api/broadcast/crew/invitations")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CrewDelivery_DeletedAccountCannotReleaseItsBoundInviteToAnotherAccount()
    {
        var setup = await SeedCrewDeliveryAsync();
        var issued = await _factory.CreateAuthenticatedClient(setup.Owner).PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/crew/invites", new { role = "observer", email = setup.Email });
        var inviteId = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteId").GetGuid();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("DELETE FROM auth.users WHERE id = @recipient", new { recipient = setup.Recipient });
        (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_crew_invites WHERE id = @inviteId)", new { inviteId })).Should().BeFalse();
    }
}
