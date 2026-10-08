using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>ADO.NET <see cref="DbParameter"/> that holds a name and a value for the command.</summary>
internal sealed class CosmosDbParameter : DbParameter
{
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    public override DbType DbType { get; set; } = DbType.Object;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    public override int Size { get; set; }
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}
