using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbCommand"/> that runs the SQL YesSql emits as Cosmos operations. Each statement is parsed into a tree
/// (see <c>Internal/Sql</c>) and dispatched on what it is: a read, a count, an insert, an update or a delete. Values come from
/// the statement's literals and from the command's parameters, which YesSql names after the columns they fill.
/// </summary>
/// <remarks>
/// Storage model (single container): each YesSql table row becomes a Cosmos item
/// <c>{ id: "&lt;table&gt;:&lt;Id&gt;", pk, __table, Id, ... }</c>. <c>pk</c> is the table name under
/// <see cref="PartitionStrategy.PerTable"/> and <see cref="CosmosDbOptions.PartitionScope"/> under
/// <see cref="PartitionStrategy.PerStore"/>. See docs/ARCHITECTURE.md.
/// </remarks>
internal sealed partial class CosmosDbCommand : DbCommand
{
    private static readonly string[] DocumentColumns = { "Id", "Type", "Content", "Version" };

    private readonly CosmosDbParameterCollection _parameters = new();
    private readonly CosmosDbConnection _connection;

    public CosmosDbCommand(CosmosDbConnection connection)
    {
        _connection = connection;
        DbConnection = connection;
    }

    [AllowNull]
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

    private Container CosmosContainer => _connection.CosmosContainer;

    // ---- partitioning (PerTable: pk = table; PerStore: pk = scope, table kept as a __table field) ----

    private string PkValue(string table)
        => _connection.Options.PartitionStrategy == PartitionStrategy.PerStore
            ? _connection.Options.PartitionScope
            : table;

    private PartitionKey PartitionKeyFor(string table) => new(PkValue(table));

    private string PartitionKeyProperty => _connection.Options.PartitionKeyProperty;

    // The active unit of work's undo log (set by YesSql on the command), or null when untracked.
    private CosmosDbTransaction? Undo => DbTransaction as CosmosDbTransaction;

    // WHERE fragment that scopes a query to one table's items (bind the named param to PkValue(table)).
    // In PerStore the single partition holds every table, so the __table discriminator is required.
    private string Scoped(string table, string pkParam = "@pk")
        => _connection.Options.PartitionStrategy == PartitionStrategy.PerStore
            ? $"c[\"{PartitionKeyProperty}\"] = {pkParam} AND c.__table = {CosmosExpressionWriter.StringLiteral(table)}"
            : $"c[\"{PartitionKeyProperty}\"] = {pkParam}";

    // Waits for the writes the open unit of work has in flight, so a query sees them.
    private Task CompleteWritesAsync() => _connection.ActiveTransaction?.CompleteWritesAsync() ?? Task.CompletedTask;

    // Waits for the write in flight to one item, so a point read of it sees that write.
    private Task CompleteWriteAsync(string itemId) => _connection.ActiveTransaction?.CompleteWriteAsync(itemId) ?? Task.CompletedTask;

    // Creates the item of an INSERT. A duplicate id is an error, as a duplicate key is in a relational database: it
    // throws CosmosDbException (409) and never replaces the item that is there. Inside a unit of work the request is
    // started and not awaited, see CosmosDbTransaction.CreateAsync.
    private async Task CreateItemAsync(JObject item, string table, CancellationToken cancellationToken)
    {
        if (Undo is { } transaction)
        {
            await transaction.CreateAsync(CosmosContainer, item, PartitionKeyFor(table), PkValue(table), cancellationToken);
            return;
        }

        await CosmosContainer.CreateItemAsync(item, PartitionKeyFor(table), cancellationToken: cancellationToken);
    }

    // Replaces an item the statement has just read, started and not awaited inside a unit of work like CreateItemAsync.
    private async Task ReplaceItemAsync(JObject item, string table, CancellationToken cancellationToken)
    {
        if (Undo is { } transaction)
        {
            await transaction.ReplaceAsync(CosmosContainer, item, PartitionKeyFor(table), cancellationToken);
            return;
        }

        await CosmosContainer.ReplaceItemAsync(item, item["id"]!.ToString(), PartitionKeyFor(table), cancellationToken: cancellationToken);
    }

    // Deletes an item, started and not awaited inside a unit of work like CreateItemAsync. An item already gone is fine.
    private async Task DeleteItemAsync(string itemId, string table, CancellationToken cancellationToken)
    {
        if (Undo is { } transaction)
        {
            await transaction.DeleteAsync(CosmosContainer, itemId, PartitionKeyFor(table), cancellationToken);
            return;
        }

        try
        {
            await CosmosContainer.DeleteItemAsync<JObject>(itemId, PartitionKeyFor(table), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // already gone
        }
    }

    // Stamp the partition key + table discriminator onto an item being written.
    private JObject WithPartition(JObject item, string table)
    {
        item[PartitionKeyProperty] = PkValue(table);
        item["__table"] = table;
        return item;
    }

    // Parameters derived while translating a statement (for example the value lists of resolved IN subqueries).
    private readonly List<(string Name, object? Value)> _derivedParameters = new();

    // Binds the command's parameters, and any derived ones, to a Cosmos query.
    private QueryDefinition BindParameters(QueryDefinition query)
    {
        foreach (DbParameter p in _parameters)
        {
            query = query.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : AsUtc(p.Value));
        }

        foreach (var (name, value) in _derivedParameters)
        {
            query = query.WithParameter(name, value);
        }

        return query;
    }

    // ---- async (primary) path, used by Dapper via QueryAsync/ExecuteAsync ----

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteNonQueryCoreAsync(cancellationToken);
        }
        catch (CosmosException ex)
        {
            throw new CosmosDbException(ex);
        }
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteScalarCoreAsync(cancellationToken);
        }
        catch (CosmosException ex)
        {
            throw new CosmosDbException(ex);
        }
    }

    private async Task<object?> ExecuteScalarCoreAsync(CancellationToken cancellationToken)
    {
        _derivedParameters.Clear();

        switch (SqlParser.ParseStatement(CommandText))
        {
            case SelectStatement select:
                await CompleteWritesAsync();
                var shape = SelectShape.Of(select, CommandText);
                switch (shape.Scalar)
                {
                    // YesSql's block id generator: SELECT [nextval] FROM [Identifiers] WHERE [dimension] = @dimension
                    case ScalarRoute.Identifier:
                        return await ReadIdentifierAsync(shape, cancellationToken);

                    // DefaultIdGenerator seed: SELECT MAX([Id]) FROM [<table>]
                    case ScalarRoute.MaxId:
                        return await MaxIdAsync(shape.RequiredFromTable, cancellationToken);

                    // A count over a join: SELECT count(distinct [Document].[Id]) FROM [Document] INNER JOIN [Index] ... WHERE ...
                    case ScalarRoute.CountJoin:
                        return await CountJoinAsync(shape, cancellationToken);

                    // A count without a join: SELECT count(*) FROM [table] [WHERE predicate], over the table's items.
                    case ScalarRoute.CountItems:
                        return await CountItemsAsync(shape, cancellationToken);
                }

                break;

            // The insert of an index row, which YesSql runs as a scalar to read the new row's id back (the statement ends
            // in RETURNING). Cosmos has no auto-increment, so the id comes from the table's counter, and every parameter
            // becomes a field of the item.
            case InsertStatement insert:
                var newId = await NextSequenceAsync(insert.Table, cancellationToken);
                var item = new JObject
                {
                    ["id"] = $"{insert.Table}:{newId}",
                    ["Id"] = newId,
                };

                foreach (DbParameter p in _parameters)
                {
                    var name = p.ParameterName.TrimStart('@');
                    item[name] = ToToken(p.Value is DBNull ? null : p.Value);
                }

                WithPartition(item, insert.Table);
                await CreateItemAsync(item, insert.Table, cancellationToken);
                return newId;
        }

        throw new NotSupportedException($"Unsupported scalar statement: {CommandText}");
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteReaderCoreAsync(cancellationToken);
        }
        catch (CosmosException ex)
        {
            throw new CosmosDbException(ex);
        }
    }

    private async Task<DbDataReader> ExecuteReaderCoreAsync(CancellationToken cancellationToken)
    {
        _derivedParameters.Clear();

        if (SqlParser.ParseStatement(CommandText) is not SelectStatement select)
        {
            throw new NotSupportedException($"Unsupported query statement: {CommandText}");
        }

        await CompleteWritesAsync();

        var shape = SelectShape.Of(select, CommandText);
        if (shape.HasOuterJoin && shape.Reader != ReaderRoute.CountJoinRow)
        {
            throw new NotSupportedException($"A LEFT or RIGHT JOIN is supported only in COUNT(1): {CommandText}");
        }

        switch (shape.Reader)
        {
            // A count over a join, run through a reader by the raw join API (Dapper's QueryFirstOrDefaultAsync<int>). The
            // count is returned as a one-row result with a "count" column.
            case ReaderRoute.CountJoinRow:
                return new CosmosDbDataReader(["count"], [[(object?)await CountJoinAsync(shape, cancellationToken)]]);

            // A reduce index query joins the document to the index through its bridge table, and is recognized by the join
            // of the index to the bridge, ON a.[Id] = b.[IndexId]. It is resolved index rows first, then bridge rows, then
            // documents.
            case ReaderRoute.ReduceJoin:
                return await DocumentsOfAsync(shape, await GatherReduceDocumentIdsAsync(shape, cancellationToken), cancellationToken);

            // A join across different index tables (.With<I1>().With<I2>()): each index's DocumentIds are found and
            // intersected, which is what an inner join does. The same index joined more than once (boolean queries) is one
            // table to the provider, see SqlTree.SplitByTable.
            case ReaderRoute.MultiIndexJoin:
                return await DocumentsOfAsync(shape, await GatherMultiIndexDocumentIdsAsync(shape, cancellationToken), cancellationToken);

            // A join of the document to one map index (JOIN [index] AS a ON a.[DocumentId] = ...), flat or inside the
            // derived table YesSql wraps a list query in.
            case ReaderRoute.IndexJoin:
                // A page of a query over one index is selected in Cosmos when it can be; the ids are then already paged.
                if (await TryPageDocumentIdsAsync(shape, cancellationToken) is { } pagedIds)
                {
                    return new CosmosDbDataReader(DocumentColumns, await ReadDocumentRowsAsync(shape.RequiredFromTable, pagedIds, cancellationToken));
                }

                return await DocumentsOfAsync(shape, await GatherDocumentIdsAsync(shape, cancellationToken), cancellationToken);

            // A join onto a subquery that has no index inside is the document-by-type form of Query<T>().ListAsync().
            case ReaderRoute.DocumentsByJoin:
            case ReaderRoute.Documents:
                return await QueryDocumentsAsync(shape, cancellationToken);

            // A count without a join, run through a reader like the one above.
            case ReaderRoute.CountRow:
                return new CosmosDbDataReader(["count"], [[(object?)await CountItemsAsync(shape, cancellationToken)]]);

            // SELECT DateTimePart("part", [col]) FROM [table]: a Cosmos VALUE query, so the computed number is returned.
            case ReaderRoute.DatePart:
                return await ExecuteDatePartAsync(shape, cancellationToken);

            // Load by key: WHERE [Id] = @Id / IN (…).
            case ReaderRoute.DocumentsById:
                return new CosmosDbDataReader(DocumentColumns, await ReadDocumentRowsAsync(shape.RequiredFromTable, KeysOf(shape.Where), cancellationToken));

            // The rows of an index table: SELECT * FROM [index] AS a [WHERE ...] [LIMIT n].
            default:
                return await QueryIndexRowsAsync(shape, cancellationToken);
        }
    }

    // ---- sync path delegates to async ----

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();
}
