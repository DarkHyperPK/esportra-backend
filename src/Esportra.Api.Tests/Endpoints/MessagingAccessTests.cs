using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class MessagingAccessTests(ApiFactory factory)
{
    private async Task<(Guid Id, HttpClient Client)> User()
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        return (id, factory.CreateAuthenticatedClient(id));
    }

    private async Task<Guid> Conversation(Guid owner, Guid participant)
    {
        var id = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.conversations (id, type, created_by) VALUES (@id, 'direct', @owner)", new { id, owner });
        await conn.ExecuteAsync("INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@id, @owner), (@id, @participant)", new { id, owner, participant });
        return id;
    }

    [Fact]
    public async Task Messages_ReturnNewestPageFirst_AndOlderPageInChronologicalOrder()
    {
        var (owner, client) = await User();
        var (participant, _) = await User();
        var conversation = await Conversation(owner, participant);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.messages (conversation_id, sender_id, content, created_at)
            SELECT @conversation, @participant, 'message-' || n, now() + n * interval '1 second'
            FROM generate_series(1, 55) n
            """, new { conversation, participant });

        var newestResponse = await client.GetAsync($"/api/conversations/{conversation}/messages?limit=50&offset=0");
        newestResponse.EnsureSuccessStatusCode();
        var newest = await newestResponse.Content.ReadFromJsonAsync<JsonElement>();
        newest.GetArrayLength().Should().Be(50);
        newest[0].GetProperty("content").GetString().Should().Be("message-6");
        newest[49].GetProperty("content").GetString().Should().Be("message-55");
        var olderResponse = await client.GetAsync($"/api/conversations/{conversation}/messages?limit=50&offset=50");
        olderResponse.EnsureSuccessStatusCode();
        var older = await olderResponse.Content.ReadFromJsonAsync<JsonElement>();
        older.GetArrayLength().Should().Be(5);
        older[0].GetProperty("content").GetString().Should().Be("message-1");
        older[4].GetProperty("content").GetString().Should().Be("message-5");
    }

    [Fact]
    public async Task Outsider_CannotJoinReadOrSendInDirectConversation()
    {
        var (owner, _) = await User();
        var (participant, _) = await User();
        var (_, outsider) = await User();
        var conversation = await Conversation(owner, participant);
        (await outsider.PostAsync($"/api/conversations/{conversation}/join", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.GetAsync($"/api/conversations/{conversation}/messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PutAsJsonAsync($"/api/conversations/{conversation}/read", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { content = "Hello" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InactiveParticipant_CannotReadOrSendUntilRejoining()
    {
        var (owner, _) = await User();
        var (participant, client) = await User();
        var conversation = await Conversation(owner, participant);
        (await client.PostAsync($"/api/conversations/{conversation}/leave", null)).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/conversations/{conversation}/messages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { content = "Hello" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/conversations/{conversation}/join", null)).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/conversations/{conversation}/messages")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { content = "Hello again" })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DirectConversation_RejectsMultipleOtherParticipants()
    {
        var (_, client) = await User();
        var (one, _) = await User();
        var (two, _) = await User();
        var response = await client.PostAsJsonAsync("/api/conversations", new { type = "direct", participantIds = new[] { one.ToString(), two.ToString() } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DatabaseRole_CannotAddOwnMembershipToAnotherConversation()
    {
        var (owner, _) = await User();
        var (participant, _) = await User();
        var (outsider, _) = await User();
        var conversation = await Conversation(owner, participant);
        await using var conn = factory.Seeder.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
        await conn.ExecuteAsync("SELECT set_config('request.jwt.claim.sub', @id, true)", new { id = outsider.ToString() }, tx);
        Func<Task> join = () => conn.ExecuteAsync("INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@conversation, @outsider)", new { conversation, outsider }, tx);
        var failure = await join.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("42501");
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task DatabaseRole_CannotSendToAnotherConversationThroughRls()
    {
        var (owner, _) = await User();
        var (participant, _) = await User();
        var (outsider, _) = await User();
        var allowedConversation = await Conversation(owner, outsider);
        var privateConversation = await Conversation(owner, participant);
        await using var conn = factory.Seeder.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("GRANT USAGE ON SCHEMA auth TO authenticated; GRANT SELECT ON public.conversation_participants TO authenticated; GRANT INSERT ON public.messages TO authenticated", transaction: tx);
        await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
        await conn.ExecuteAsync("SELECT set_config('request.jwt.claim.sub', @id, true)", new { id = outsider.ToString() }, tx);
        await conn.ExecuteAsync("INSERT INTO public.messages (conversation_id, sender_id, content) VALUES (@allowedConversation, @outsider, 'allowed')", new { allowedConversation, outsider }, tx);
        Func<Task> send = () => conn.ExecuteAsync("INSERT INTO public.messages (conversation_id, sender_id, content) VALUES (@privateConversation, @outsider, 'forbidden')", new { privateConversation, outsider }, tx);
        var failure = await send.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("42501");
        await tx.RollbackAsync();
    }

    [Theory]
    [InlineData("system", false)]
    [InlineData("text", true)]
    public async Task DatabaseRole_CannotForgeListingReferences(string messageType, bool withInterest)
    {
        var (owner, _) = await User();
        var (participant, _) = await User();
        var conversation = await Conversation(owner, participant);
        await using var conn = factory.Seeder.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("GRANT USAGE ON SCHEMA auth TO authenticated; GRANT SELECT ON public.conversation_participants TO authenticated; GRANT INSERT ON public.messages TO authenticated", transaction: tx);
        await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
        await conn.ExecuteAsync("SELECT set_config('request.jwt.claim.sub', @id, true)", new { id = participant.ToString() }, tx);
        Func<Task> forge = () => conn.ExecuteAsync("INSERT INTO public.messages (conversation_id, sender_id, content, message_type, lfg_interest_id) VALUES (@conversation, @participant, 'Fake listing', @messageType, @interest)",
            new { conversation, participant, messageType, interest = withInterest ? (Guid?)Guid.NewGuid() : null }, tx);
        (await forge.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        await tx.RollbackAsync();
    }
}
