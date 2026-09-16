#pragma warning disable CS8767 // ADO.NET abstract members were annotated inconsistently across .NET versions

using System.Collections;
using System.Data;
using System.Data.Common;
using Esportra.Contracts.Database;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// Minimal in-process fake for <see cref="IDbConnectionFactory"/> that lets tests
/// pre-load ordered query results without a real database.
/// Uses <see cref="DbConnection"/> / <see cref="DbCommand"/> base classes so that
/// Dapper 2.x async APIs work correctly.
/// </summary>
internal sealed class FakeDbConnectionFactory : IDbConnectionFactory
{
    private readonly Queue<object?[][]> _queue = new();

    public int QueueCount => _queue.Count;

    public void EnqueueSingleRowResult(params object?[] values)
        => _queue.Enqueue([values]);

    public void EnqueueMultiRowResult(IEnumerable<object?[]> rows)
        => _queue.Enqueue(rows.ToArray());

    public void EnqueueEmptyResult()
        => _queue.Enqueue([]);

    public IDbConnection CreateConnection() => new FakeDbConnection(_queue);
}

// ── Connection ────────────────────────────────────────────────────────────────

internal sealed class FakeDbConnection(Queue<object?[][]> queue) : DbConnection
{
    public override string ConnectionString { get; set; } = "fake";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => ConnectionState.Open;

    public override void Open() { }
    public override void Close() { }
    public override void ChangeDatabase(string databaseName) { }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeDbCommand(queue);
}

// ── Command ───────────────────────────────────────────────────────────────────

internal sealed class FakeDbCommand(Queue<object?[][]> queue) : DbCommand
{
    private readonly FakeDbParameterCollection _params = new();

    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbParameterCollection DbParameterCollection => _params;
    protected override DbTransaction? DbTransaction { get; set; }
    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var rows = queue.Count > 0 ? queue.Dequeue() : [];
        return new FakeDbDataReader(rows);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => Task.FromResult(ExecuteDbDataReader(behavior));

    public override void Cancel() { }
    public override int ExecuteNonQuery() => 0;
    public override object? ExecuteScalar() => null;
    public override void Prepare() { }
}

// ── DataReader ────────────────────────────────────────────────────────────────

internal sealed class FakeDbDataReader(object?[][] rows) : DbDataReader
{
    private int _rowIndex = -1;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => throw new NotSupportedException();

    public override int FieldCount => rows.Length > 0 ? rows[0].Length : 0;
    public override bool HasRows => rows.Length > 0;
    public override bool IsClosed => _rowIndex >= rows.Length;
    public override int RecordsAffected => 0;
    public override int Depth => 0;

    public override bool Read() => ++_rowIndex < rows.Length;
    public override bool NextResult() => false;

    public override object GetValue(int ordinal) => rows[_rowIndex][ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => rows[_rowIndex][ordinal] is null or DBNull;

    public override string GetName(int ordinal) => $"col{ordinal}";
    public override int GetOrdinal(string name) => 0;
    public override string GetDataTypeName(int ordinal) => "unknown";
    public override Type GetFieldType(int ordinal) => typeof(object);
    public override int GetValues(object[] values) => 0;

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_rowIndex][ordinal]);
    public override byte GetByte(int ordinal) => Convert.ToByte(rows[_rowIndex][ordinal]);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => Convert.ToChar(rows[_rowIndex][ordinal]!);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override DateTime GetDateTime(int ordinal) => (DateTime)rows[_rowIndex][ordinal]!;
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_rowIndex][ordinal]);
    public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_rowIndex][ordinal]);
    public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_rowIndex][ordinal]);
    public override Guid GetGuid(int ordinal) => (Guid)rows[_rowIndex][ordinal]!;
    public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_rowIndex][ordinal]);
    public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_rowIndex][ordinal]);
    public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_rowIndex][ordinal]);
    public override string GetString(int ordinal) => (string)rows[_rowIndex][ordinal]!;

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}

// ── Parameter + ParameterCollection ──────────────────────────────────────────

internal sealed class FakeDbParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; } = string.Empty;
    public override int Size { get; set; }
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => this;

    public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
    public override void AddRange(Array values) { foreach (DbParameter p in values) _items.Add(p); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAll(p => p.ParameterName == parameterName);

    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName)
        => _items.First(p => p.ParameterName == parameterName);
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var i = IndexOf(parameterName);
        if (i >= 0) _items[i] = value;
    }
}
