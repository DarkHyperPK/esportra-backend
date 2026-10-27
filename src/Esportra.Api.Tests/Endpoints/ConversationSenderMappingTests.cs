using System.Data;
using Dapper;
using Esportra.Api.Hubs;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed class ConversationSenderMappingTests
{
    [Fact]
    public void SenderProjection_WithUuidIdCannotMapToStringConstructor()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(Guid));
        table.Columns.Add("username", typeof(string));
        table.Columns.Add("fullname", typeof(string));
        table.Columns.Add("avatarurl", typeof(string));
        table.Rows.Add(Guid.NewGuid(), "player", "Player Name", DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read().Should().BeTrue();

        Action parse = () => reader.GetRowParser<ConversationSenderDto>()(reader);
        parse.Should().Throw<InvalidOperationException>().WithMessage("*constructor*");
    }

    [Fact]
    public void SenderProjection_MapsTextIdToSignalRDto()
    {
        var id = Guid.NewGuid().ToString();
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("username", typeof(string));
        table.Columns.Add("fullname", typeof(string));
        table.Columns.Add("avatarurl", typeof(string));
        table.Rows.Add(id, "player", "Player Name", DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read().Should().BeTrue();

        var sender = reader.GetRowParser<ConversationSenderDto>()(reader);

        sender.Id.Should().Be(id);
        sender.Username.Should().Be("player");
        sender.FullName.Should().Be("Player Name");
        sender.AvatarUrl.Should().BeNull();
    }
}
