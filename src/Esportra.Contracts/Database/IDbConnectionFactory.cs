using System.Data;

namespace Esportra.Contracts.Database;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
