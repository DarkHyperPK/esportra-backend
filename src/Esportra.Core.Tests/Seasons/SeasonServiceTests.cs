using Xunit;
using Moq;
using Dapper;
using System.Text.Json;
using Esportra.Core.Seasons;

namespace Esportra.Core.Tests.Seasons;

public class SeasonServiceTests
{
    private readonly Mock<IDbConnectionFactory> mockDb;
    private readonly Mock<SeasonAuditService> mockAuditService;
    private readonly SeasonService seasonService;

    public SeasonServiceTests()
    {
        mockDb = new Mock<IDbConnectionFactory>();
        mockAuditService = new Mock<SeasonAuditService>();
        seasonService = new SeasonService(mockDb.Object, mockAuditService.Object);
    }

    [Fact]
    public async Task CreateSeasonAsync_ShouldInsertSeasonAndLogAudit()
    {
        // Arrange
        var seasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = new Esportra.Contracts.Auth.UserContext(userId, "127.0.0.1", "TestAgent");

        var mockConnection = new Mock<System.Data.IDbConnection>();
        mockDb.Setup(x => x.CreateConnection()).Returns(mockConnection.Object);
        mockConnection.Setup(x => x.ExecuteScalarAsync<Guid>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()))
            .ReturnsAsync(seasonId);

        // Act
        var result = await seasonService.CreateSeasonAsync(
            "Test Season",
            "test-season",
            "Test Description",
            "valorant",
            "team",
            userId,
            null,
            true,
            true,
            null,
            null,
            actor,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(seasonId, result);
        mockConnection.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()), Times.Once);
        mockAuditService.Verify(x => x.LogAsync(
            seasonId,
            userId,
            "season_created",
            "season",
            seasonId,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task UpdateSeasonAsync_ShouldUpdateSeasonAndLogAudit()
    {
        // Arrange
        var seasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = new Esportra.Contracts.Auth.UserContext(userId, "127.0.0.1", "TestAgent");

        var mockConnection = new Mock<System.Data.IDbConnection>();
        mockDb.Setup(x => x.CreateConnection()).Returns(mockConnection.Object);
        mockConnection.Setup(x => x.ExecuteScalarAsync<int>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()))
            .ReturnsAsync(1);

        // Act
        await seasonService.UpdateSeasonAsync(
            seasonId,
            name: "Updated Season",
            slug: "updated-season",
            description: "Updated Description",
            game: null,
            participantMode: null,
            startDate: null,
            endDate: null,
            actor,
            CancellationToken.None
        );

        // Assert
        mockConnection.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()), Times.Once);
        mockAuditService.Verify(x => x.LogAsync(
            seasonId,
            userId,
            "season_updated",
            "season",
            seasonId,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task SoftDeleteSeasonAsync_ShouldMarkDeletedAndLogAudit()
    {
        // Arrange
        var seasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = new Esportra.Contracts.Auth.UserContext(userId, "127.0.0.1", "TestAgent");

        var mockConnection = new Mock<System.Data.IDbConnection>();
        mockDb.Setup(x => x.CreateConnection()).Returns(mockConnection.Object);
        mockConnection.Setup(x => x.ExecuteScalarAsync<int>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()))
            .ReturnsAsync(1);

        // Act
        await seasonService.SoftDeleteSeasonAsync(seasonId, actor, CancellationToken.None);

        // Assert
        mockConnection.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()), Times.Once);
        mockAuditService.Verify(x => x.LogAsync(
            seasonId,
            userId,
            "season_deleted",
            "season",
            seasonId,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task DuplicateSeasonAsync_ShouldDuplicateSeasonWithAudit()
    {
        // Arrange
        var sourceSeasonId = Guid.NewGuid();
        var newSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = new Esportra.Contracts.Auth.UserContext(userId, "127.0.0.1", "TestAgent");

        var mockConnection = new Mock<System.Data.IDbConnection>();
        mockDb.Setup(x => x.CreateConnection()).Returns(mockConnection.Object);
        mockConnection.Setup(x => x.ExecuteScalarAsync<Guid>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()))
            .ReturnsAsync(newSeasonId);

        // Act
        var result = await seasonService.DuplicateSeasonAsync(
            sourceSeasonId,
            "Duplicated Season",
            "duplicated-season",
            actor,
            CancellationToken.None
        );

        // Assert
        Assert.Equal(newSeasonId, result);
        mockConnection.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.IDbTransaction?>()), Times.AtLeastOnce);
        mockAuditService.Verify(x => x.LogAsync(
            It.IsAny<Guid>(),
            userId,
            "season_duplicated",
            "season",
            newSeasonId,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }
}
