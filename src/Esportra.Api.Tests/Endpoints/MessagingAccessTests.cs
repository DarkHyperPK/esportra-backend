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
    public async Task ReadReceipt_StopsAtVisibleMessage_AndNeverMovesBackwards()
    {
        var (sender, senderClient) = await User();
        var (reader, readerClient) = await User();
        var (_, outsider) = await User();
        var conversation = await Conversation(sender, reader);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.messages (id, conversation_id, sender_id, content, created_at)
            VALUES (@first, @conversation, @sender, 'first', now() - interval '2 minutes'),
                   (@second, @conversation, @sender, 'second', now() - interval '1 minute')
            """, new { first, second, conversation, sender });

        (await outsider.PutAsJsonAsync($"/api/conversations/{conversation}/read?through={second}", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await readerClient.PutAsJsonAsync($"/api/conversations/{conversation}/read?through={Guid.NewGuid()}", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await readerClient.PutAsJsonAsync($"/api/conversations/{conversation}/read?through={first}", new { })).EnsureSuccessStatusCode();
        var atFirst = await conn.ExecuteScalarAsync<DateTime>("SELECT last_read_at FROM public.conversation_participants WHERE conversation_id = @conversation AND user_id = @reader", new { conversation, reader });
        (await conn.ExecuteScalarAsync<DateTime>("SELECT created_at FROM public.messages WHERE id = @first", new { first })).Should().Be(atFirst);
        var list = await (await readerClient.GetAsync("/api/conversations")).Content.ReadFromJsonAsync<JsonElement>();
        list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == conversation).GetProperty("unread_count").GetInt32().Should().Be(1);
        (await readerClient.PutAsJsonAsync($"/api/conversations/{conversation}/read?through={second}", new { })).EnsureSuccessStatusCode();
        (await readerClient.PutAsJsonAsync($"/api/conversations/{conversation}/read?through={first}", new { })).EnsureSuccessStatusCode();
        var atSecond = await conn.ExecuteScalarAsync<DateTime>("SELECT last_read_at FROM public.conversation_participants WHERE conversation_id = @conversation AND user_id = @reader", new { conversation, reader });
        atSecond.Should().BeAfter(atFirst);
        var senderList = await (await senderClient.GetAsync("/api/conversations")).Content.ReadFromJsonAsync<JsonElement>();
        var participants = senderList.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == conversation).GetProperty("participants");
        participants.EnumerateArray().Single(p => p.GetProperty("user_id").GetGuid() == reader).GetProperty("last_read_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task LfgNotification_IncludesRequestersPublicAvatar_ForOwnerOnly()
    {
        var (owner, ownerClient) = await User();
        var (requester, _) = await User();
        var (_, outsider) = await User();
        var listing = Guid.NewGuid();
        var interest = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            UPDATE public.profiles SET username = 'avatarrequester', avatar_url = 'https://example.test/avatar.png' WHERE id = @requester;
            INSERT INTO public.lfg_listings (id, user_id, game_slug, intent, status)
            VALUES (@listing, @owner, 'valorant', 'looking_for_team', 'published');
            INSERT INTO public.lfg_interests (id, listing_id, from_user_id, to_user_id, status)
            VALUES (@interest, @listing, @requester, @owner, 'expressed');
            INSERT INTO public.notifications (user_id, type, title, message, data, is_read)
            VALUES (@owner, 'lfg_interest_received', 'Request', 'Interested', jsonb_build_object('interest_id', @interest::text), FALSE)
            """, new { owner, requester, listing, interest });
        var notification = await (await ownerClient.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        var item = notification.GetProperty("notifications").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "lfg_interest_received");
        item.GetProperty("actor_name").GetString().Should().Be("avatarrequester");
        item.GetProperty("actor_avatar_url").GetString().Should().Be("https://example.test/avatar.png");
        var other = await (await outsider.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        other.GetProperty("notifications").EnumerateArray().Should().NotContain(n => n.GetProperty("type").GetString() == "lfg_interest_received");
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
