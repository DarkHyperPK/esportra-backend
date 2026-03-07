using System.Data;
using Npgsql;

namespace Esportra.Infrastructure.Database;

public sealed class NpgsqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString;

    public IDbConnection CreateConnection()
        => new NpgsqlConnection(_connectionString);
}
