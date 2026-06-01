using System;
using System.Data;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbCommand"/> shim. Receives the constrained SQL produced by
/// <see cref="CosmosDbDialect"/> and YesSql's command classes, and (in the next milestone) translates
/// the bounded set of statement shapes — document INSERT/UPDATE/DELETE and document SELECT-by-id /
/// SELECT-with-filter — into Cosmos SDK operations.
/// </summary>
public sealed class CosmosDbCommand : DbCommand
{
    private readonly CosmosDbParameterCollection _parameters = new();

    public CosmosDbCommand(CosmosDbConnection connection)
    {
        DbConnection = connection;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new CosmosDbParameter();

    // TODO (Milestone 2b): parse CommandText (INSERT into [Document] …) and upsert the document.
    public override int ExecuteNonQuery()
        => throw new NotImplementedException("CosmosDbCommand.ExecuteNonQuery — write-path translation pending.");

    // TODO (Milestone 2b): map IdentityLastId / scalar selects.
    public override object ExecuteScalar()
        => throw new NotImplementedException("CosmosDbCommand.ExecuteScalar — scalar translation pending.");

    // TODO (Milestone 2c): translate SELECT … from [Document] where … into a Cosmos query and surface
    // rows via a CosmosDbDataReader.
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => throw new NotImplementedException("CosmosDbCommand.ExecuteDbDataReader — read-path translation pending.");
}
