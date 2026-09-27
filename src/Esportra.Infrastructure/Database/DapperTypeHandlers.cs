using System.Data;
using Dapper;

namespace Esportra.Infrastructure.Database;

public sealed class IntArrayTypeHandler : SqlMapper.TypeHandler<int[]>
{
    public override int[] Parse(object value) => (int[])value;

    public override void SetValue(IDbDataParameter parameter, int[]? value)
        => parameter.Value = value;
}
