using System.Data;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbTransaction"/> shim. A YesSql unit of work buffers its document/index mutations
/// and (in a later milestone) commits them atomically via a partition-scoped Cosmos stored procedure.
/// For now it tracks the connection and isolation level; buffering/commit lands with the write path.
/// </summary>
public sealed class CosmosDbTransaction : DbTransaction
{
    private readonly CosmosDbConnection _connection;

    public CosmosDbTransaction(CosmosDbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    protected override DbConnection DbConnection => _connection;
    public override IsolationLevel IsolationLevel { get; }

    // TODO (Milestone 2b): flush the buffered write batch via stored procedure.
    public override void Commit() { }
    public override void Rollback() { }
}
