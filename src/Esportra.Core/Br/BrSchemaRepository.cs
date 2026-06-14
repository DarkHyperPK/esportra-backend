using System.Data;
using Dapper;

namespace Esportra.Core.Br;

/// <summary>
/// Single choke point for BR schema-compat introspection during the lobby → games migration.
/// </summary>
public static class BrSchemaRepository
{
    public static Task<bool> TableExistsAsync(
        IDbConnection conn,
        string tableName,
        IDbTransaction? tx = null) =>
        conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = @tableName
            )
            """,
            new { tableName },
            tx);

    public static Task<bool> ColumnExistsAsync(
        IDbConnection conn,
        string tableName,
        string columnName,
        IDbTransaction? tx = null) =>
        conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName
            )
            """,
            new { tableName, columnName },
            tx);

    public static async Task<bool> BrGamesModelReadyAsync(IDbConnection conn, IDbTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, "br_games", tx))
            return false;

        return await ColumnExistsAsync(conn, "br_lobby_evidence", "game_id", tx);
    }
}
