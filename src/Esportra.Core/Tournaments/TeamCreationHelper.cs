using System.Data;
using Dapper;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Centralized inserts for real-team adapters used by tournament registration flows.
/// </summary>
public static class TeamCreationHelper
{
    public sealed record SoloAdapterParams(
        Guid Id,
        string Name,
        string Game,
        Guid OwnerId,
        string? LogoUrl);

    public sealed record MockTeamParams(
        Guid MockId,
        string TeamName,
        string Tag,
        string Game,
        Guid OwnerId,
        bool IsSolo,
        int MaxMembers);

    public static string BuildSoloAdapterTag(Guid id) => $"solo-{id:N}";

    public static string BuildMockTag(Guid mockId) => $"mock-{mockId:N}"[..18];

    /// <summary>
    /// Legacy solo adapter team creation. Do not use for new solo registrations — use native
    /// tournament_participants with team_id NULL and participant id in bracket slots.
    /// </summary>
    [Obsolete("Solo registration uses native tournament_participants; kept for backward compatibility only.")]
    public static async Task<Guid> CreateSoloAdapterTeamAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        SoloAdapterParams team,
        Guid captainUserId,
        CancellationToken ct = default)
    {
        var tag = BuildSoloAdapterTag(team.Id);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO teams (id, name, tag, game, owner_id, is_solo, max_members, logo_url, team_kind, game_format)
            VALUES (@Id, @Name, @tag, @Game, @OwnerId, true, 1, @LogoUrl, 'solo', NULL)
            """,
            new { team.Id, team.Name, tag, team.Game, team.OwnerId, team.LogoUrl },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO team_members (team_id, user_id, role, is_active)
            VALUES (@teamId, @userId, 'captain', true)
            ON CONFLICT DO NOTHING
            """,
            new { teamId = team.Id, userId = captainUserId },
            tx,
            cancellationToken: ct));

        return team.Id;
    }

    public static async Task UpsertMockTeamsAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        IEnumerable<MockTeamParams> rows,
        CancellationToken ct = default)
    {
        var list = rows as IList<MockTeamParams> ?? rows.ToList();
        if (list.Count == 0) return;

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO teams (id, name, tag, game, owner_id, is_solo, max_members, team_kind, game_format)
            VALUES (@MockId, @TeamName, @Tag, @Game, @OwnerId, @IsSolo, @MaxMembers, 'mock', NULL)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                tag = EXCLUDED.tag,
                game = EXCLUDED.game,
                owner_id = EXCLUDED.owner_id,
                is_solo = EXCLUDED.is_solo,
                max_members = EXCLUDED.max_members,
                team_kind = 'mock',
                game_format = NULL
            """,
            list,
            tx,
            cancellationToken: ct));
    }
}
