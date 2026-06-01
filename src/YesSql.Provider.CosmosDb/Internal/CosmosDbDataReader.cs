using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// Minimal forward-only <see cref="DbDataReader"/> over an in-memory result set, sufficient for the
/// way YesSql/Dapper materialize <c>Document</c> rows (column-name → value mapping). Columns are the
/// document fields <c>Id</c>, <c>Type</c>, <c>Content</c>, <c>Version</c>.
/// </summary>
public sealed class CosmosDbDataReader : DbDataReader
{
    private readonly string[] _columns;
    private readonly Dictionary<string, int> _ordinals;
    private readonly IReadOnlyList<object?[]> _rows;
    private int _index = -1;

    public CosmosDbDataReader(string[] columns, IReadOnlyList<object?[]> rows)
    {
        _columns = columns;
        _rows = rows;
        _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Length; i++)
        {
            _ordinals[columns[i]] = i;
        }
    }

    public override int FieldCount => _columns.Length;
    public override bool HasRows => _rows.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_index < _rows.Count;
    public override bool NextResult() => false;

    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name)
        => _ordinals.TryGetValue(name, out var i) ? i : throw new IndexOutOfRangeException(name);

    public override object GetValue(int ordinal) => _rows[_index][ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => _rows[_index][ordinal] is null;

    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, _columns.Length);
        for (var i = 0; i < n; i++)
        {
            values[i] = GetValue(i);
        }

        return n;
    }

    public override Type GetFieldType(int ordinal)
    {
        var v = _index >= 0 && _index < _rows.Count ? _rows[_index][ordinal] : null;
        return v?.GetType() ?? typeof(object);
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal))!;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => _rows.GetEnumerator();
}
