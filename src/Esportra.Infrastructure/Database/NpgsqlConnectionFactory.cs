using System.Data;
using Esportra.Contracts.Database;
using Npgsql;

namespace Esportra.Infrastructure.Database;

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableUnmappedTypes();
        _dataSource = builder.Build();
    }

    public IDbConnection CreateConnection()
    {
        var conn = _dataSource.CreateConnection();
        conn.Open();
        return conn;
    }
}
