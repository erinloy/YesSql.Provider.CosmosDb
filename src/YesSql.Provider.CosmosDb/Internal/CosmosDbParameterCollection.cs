using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>List-backed ADO.NET <see cref="DbParameterCollection"/> for the Cosmos command shim.</summary>
public sealed class CosmosDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = new();

    public override int Count => _parameters.Count;
    public override object SyncRoot { get; } = new object();

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values)
        {
            _parameters.Add((DbParameter)v);
        }
    }

    public override void Clear() => _parameters.Clear();
    public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName)
        => _parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _parameters.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        var i = IndexOf(parameterName);
        if (i >= 0)
        {
            _parameters.RemoveAt(i);
        }
    }

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        var i = IndexOf(parameterName);
        if (i < 0)
        {
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
        }

        return _parameters[i];
    }

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var i = IndexOf(parameterName);
        if (i < 0)
        {
            _parameters.Add(value);
        }
        else
        {
            _parameters[i] = value;
        }
    }
}
