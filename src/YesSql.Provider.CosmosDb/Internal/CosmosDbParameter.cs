using System.Data;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>ADO.NET <see cref="DbParameter"/> shim — a plain value holder for the command translator.</summary>
public sealed class CosmosDbParameter : DbParameter
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    public override DbType DbType { get; set; } = DbType.Object;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    public override int Size { get; set; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}
