using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbCommand"/> shim that translates the bounded SQL surface YesSql emits into Cosmos
/// SDK operations. Statements are dispatched on their leading keyword; values are read from the
/// <see cref="DbParameterCollection"/> (Id/Type/Content/Version) rather than by parsing clauses.
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
            ? $"c[\"{PartitionKeyProperty}\"] = {pkParam} AND c.__table = \"{table}\""
            : $"c[\"{PartitionKeyProperty}\"] = {pkParam}";

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
                var shape = SelectShape.Of(select, CommandText);
                switch (shape.Scalar)
                {
                    // DefaultIdGenerator seed: SELECT MAX([Id]) FROM [<table>]
                    case ScalarRoute.MaxId:
                        return await MaxIdAsync(shape.RequiredFromTable, cancellationToken);

                    // CountAsync over an index join: SELECT count(distinct [Document].[Id]) FROM [Document] INNER
                    // JOIN [Index] … WHERE … → count the matching DocumentIds.
                    case ScalarRoute.CountJoin:
                        return await CountJoinAsync(shape, cancellationToken);

                    // CountAsync without a join: SELECT count(*) FROM [<table>] [WHERE <predicate>] — count items in
                    // that partition (documents by Type, or index rows).
                    case ScalarRoute.CountItems:
                        return await CountItemsAsync(shape, cancellationToken);
                }

                break;

            // Map-index write: insert into [<index>] ([Col]…) values (@Col…) — executed as scalar to return the new
            // index row Id. Cosmos has no auto-increment, so allocate Id from the table's sequence and store every
            // parameter as a field on the index item.
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
                await CosmosContainer.UpsertItemAsync(item, PartitionKeyFor(insert.Table), cancellationToken: cancellationToken);
                Undo?.Record(item["id"]!.ToString(), PkValue(insert.Table), null);
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

        var shape = SelectShape.Of(select, CommandText);
        switch (shape.Reader)
        {
            // A COUNT over a join run through the reader (raw Dapper QueryFirstOrDefaultAsync<int>, e.g. the
            // Inner/Left/Right join count API) — compute the matching-DocumentId count and yield it as a single
            // "count" column.
            case ReaderRoute.CountJoinRow:
                return new CosmosDbDataReader(["count"], [[(object?)await CountJoinAsync(shape, cancellationToken)]]);

            // Reduce-index query — a doc↔bridge↔index three-way join, recognised by the index↔bridge join
            // "ON a.[Id] = b.[<X>Id]". Resolve via index → bridge → documents.
            case ReaderRoute.ReduceJoin:
                return await DocumentsOfAsync(shape, await GatherReduceDocumentIdsAsync(shape, cancellationToken), cancellationToken);

            // Multi-index join across DISTINCT index tables (.With<I1>().With<I2>()) — intersect each index's
            // DocumentId set (INNER JOIN = AND). The same index joined repeatedly (scope / boolean queries) stays on
            // the single-index path, where its combined WHERE translates correctly.
            case ReaderRoute.MultiIndexJoin:
                return await DocumentsOfAsync(shape, await GatherMultiIndexDocumentIdsAsync(shape, cancellationToken), cancellationToken);

            // An index join ("JOIN [index] AS a ON a.[DocumentId] = …") — whether flat (FirstOrDefault) or wrapped in
            // a "(SELECT … GROUP BY …)" dedup subquery (ListAsync) — is an index query.
            case ReaderRoute.IndexJoin:
                return await DocumentsOfAsync(shape, await GatherDocumentIdsAsync(shape, cancellationToken), cancellationToken);

            // A join onto a "(SELECT … )" subquery with no index inside is the document-by-type form of
            // Query<T>().ListAsync().
            case ReaderRoute.DocumentsByJoin:
            case ReaderRoute.Documents:
                return await QueryDocumentsAsync(shape, cancellationToken);

            // A non-join COUNT executed through a reader (e.g. raw Dapper QueryFirstOrDefaultAsync<int>) rather than
            // ExecuteScalar — return the scalar count as a single "count" column so the reader yields it.
            case ReaderRoute.CountRow:
                return new CosmosDbDataReader(["count"], [[(object?)await CountItemsAsync(shape, cancellationToken)]]);

            // Scalar date-part projection: SELECT DateTimePart("<part>", [<col>]) FROM [<table>] — run it as a Cosmos
            // VALUE query over the partition so the computed int is returned, not a raw column.
            case ReaderRoute.DatePart:
                return await ExecuteDatePartAsync(shape, cancellationToken);

            // Load by id(s): WHERE [Id] = @Id / IN (…) — the only params are ids; point-read each.
            case ReaderRoute.DocumentsById:
                var ids = new List<long>();
                foreach (DbParameter p in _parameters)
                {
                    if (p.Value is not (null or DBNull))
                    {
                        ids.Add(Convert.ToInt64(p.Value));
                    }
                }

                return new CosmosDbDataReader(DocumentColumns, await ReadDocumentRowsAsync(shape.RequiredFromTable, ids, cancellationToken));

            // Index-row query: SELECT * FROM [index] AS a [WHERE …] [LIMIT n] → return index items.
            default:
                return await QueryIndexRowsAsync(shape, cancellationToken);
        }
    }

    // The documents with the given ids, on the requested page of them, as the result of a query over the document table.
    private async Task<DbDataReader> DocumentsOfAsync(SelectShape shape, List<long> documentIds, CancellationToken cancellationToken)
    {
        var rows = await ReadDocumentRowsAsync(shape.RequiredFromTable, PageOf(documentIds, shape), cancellationToken);
        return new CosmosDbDataReader(DocumentColumns, rows);
    }

    // ---- sync path delegates to async ----

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    // ---- helpers ----

    // Number of point reads in flight at once when loading a page of documents.
    private const int PointReadConcurrency = 8;

    // The ids on the requested page of an id list, after applying the statement's OFFSET and LIMIT.
    private static List<long> PageOf(List<long> ids, SelectShape shape)
    {
        IEnumerable<long> page = ids.Skip(ClampToInt(shape.Offset));
        if (shape.Limit is { } limit)
        {
            page = page.Take(ClampToInt(limit));
        }

        return page.ToList();
    }

    // Paging counts beyond what a list can hold are the same as no bound.
    private static int ClampToInt(long value) => (int)Math.Min(value, int.MaxValue);

    // Reads documents by id as result rows, in the order of ids; ids with no document are skipped. The reads run
    // concurrently up to a limit, since a page would otherwise cost one sequential round trip per document.
    private async Task<List<object?[]>> ReadDocumentRowsAsync(string table, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var items = new JObject?[ids.Count];
        using var gate = new SemaphoreSlim(PointReadConcurrency);

        await Task.WhenAll(ids.Select(async (id, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var response = await CosmosContainer.ReadItemAsync<JObject>($"{table}:{id}", PartitionKeyFor(table), cancellationToken: cancellationToken);
                items[index] = response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // no document for this id
            }
            finally
            {
                gate.Release();
            }
        }));

        return items.Where(item => item is not null).Select(item => ToRow(item!)).ToList();
    }

    // Parse an index-joined query and run the index lookup, returning distinct DocumentIds (ordered if
    // the query has an ORDER BY). Shared by the reader (then point-reads) and CountAsync.
    private async Task<List<long>> GatherDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        // Accept both the .With() form ("… = [Document].[Id]") and the raw SqlBuilder join form ("… = d.[Id]",
        // aliased) so InnerJoin/LeftJoin/RightJoin over Document⋈Index work.
        var join = shape.LinkJoin ?? throw new NotSupportedException($"Unsupported join query: {CommandText}");
        var indexTable = join.Table;

        // WHERE predicate over index columns → Cosmos predicate. Strip the document-Type predicate YesSql adds (it
        // does not apply inside the index partition), then rewrite column refs.
        var cosmosWhere = string.Empty;
        var predicate = SqlTree.WithoutDocumentTypePredicate(shape.Where);
        if (predicate is not null)
        {
            cosmosWhere = await WriteWhereAsync(predicate, cancellationToken);
        }

        // Ordering: Cosmos ORDER BY is case-sensitive and can't ORDER BY LOWER(...), so when the query is
        // ordered we fetch DocumentId + the order columns and sort client-side (case-insensitive, matching
        // the reference dialects). Unordered queries keep the cheap "SELECT VALUE c.DocumentId".
        var orderTerms = shape.Order;
        // DocumentId is already projected, so don't re-select it (Cosmos rejects the duplicate property).
        var extraOrderCols = orderTerms.Select(t => t.Column).Distinct()
            .Where(col => col != SelectShape.RandomColumn && !col.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)).ToList();
        var projection = orderTerms.Count == 0
            ? "VALUE c.DocumentId"
            : "c.DocumentId" + string.Concat(extraOrderCols.Select(col => ", " + CosmosExpressionWriter.Property(col)));

        var queryText = "SELECT " + projection + " FROM c WHERE " + Scoped(indexTable)
            + (cosmosWhere.Length > 0 ? " AND " + cosmosWhere : string.Empty);
        var queryDef = new QueryDefinition(queryText).WithParameter("@pk", PkValue(indexTable));
        queryDef = BindParameters(queryDef);

        var documentIds = new System.Collections.Generic.List<long>();
        var seenDocumentIds = new System.Collections.Generic.HashSet<long>();
        if (orderTerms.Count == 0)
        {
            using var iterator = CosmosContainer.GetItemQueryIterator<long>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(indexTable) });
            while (iterator.HasMoreResults)
            {
                foreach (var docId in await iterator.ReadNextAsync(cancellationToken))
                {
                    if (seenDocumentIds.Add(docId))
                    {
                        documentIds.Add(docId);
                    }
                }
            }
        }
        else
        {
            var rows = new System.Collections.Generic.List<JObject>();
            using var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(indexTable) });
            while (iterator.HasMoreResults)
            {
                foreach (var row in await iterator.ReadNextAsync(cancellationToken))
                {
                    rows.Add(row);
                }
            }

            foreach (var row in OrderRows(rows, orderTerms))
            {
                var docId = row["DocumentId"]!.ToObject<long>();
                if (seenDocumentIds.Add(docId))
                {
                    documentIds.Add(docId);
                }
            }
        }

        // filterType:true adds a "[Document].[Type] = @p" predicate that WithoutDocumentTypePredicate removed (it can't
        // run inside the index partition). Re-apply it: keep only gathered ids whose document has that exact Type.
        // Without this, a Query<SubClass>(filterType:true) counts every subclass, not just one.
        var typeParameter = SqlTree.DocumentTypeParameter(shape.Where);
        if (typeParameter is not null && documentIds.Count > 0)
        {
            object? typeValue = null;
            foreach (DbParameter p in _parameters)
            {
                if (p.ParameterName.TrimStart('@').Equals(typeParameter, StringComparison.OrdinalIgnoreCase))
                {
                    typeValue = p.Value is DBNull ? null : p.Value;
                    break;
                }
            }

            var docTable = shape.RequiredFromTable;
            var matching = new System.Collections.Generic.HashSet<long>();
            var typeQuery = new QueryDefinition("SELECT VALUE c.Id FROM c WHERE " + Scoped(docTable) + " AND c.Type = @__type AND ARRAY_CONTAINS(@__ids, c.Id)")
                .WithParameter("@pk", PkValue(docTable))
                .WithParameter("@__type", typeValue)
                .WithParameter("@__ids", documentIds);
            using var typeIterator = CosmosContainer.GetItemQueryIterator<long>(typeQuery,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(docTable) });
            while (typeIterator.HasMoreResults)
            {
                foreach (var id in await typeIterator.ReadNextAsync(cancellationToken))
                {
                    matching.Add(id);
                }
            }

            // Preserve the original (ORDER BY) sequence — keep matching ids in place, drop the rest.
            documentIds = documentIds.Where(matching.Contains).ToList();
        }

        return documentIds;
    }

    // Order comparison matching the reference dialects: nulls first, numbers numerically, everything else
    // as a case-insensitive string (ISO date strings sort chronologically under ordinal comparison).
    private static int CompareTokens(JToken? a, JToken? b)
    {
        var aNull = a is null || a.Type == JTokenType.Null;
        var bNull = b is null || b.Type == JTokenType.Null;
        if (aNull || bNull)
        {
            return aNull == bNull ? 0 : aNull ? -1 : 1;
        }

        var aNum = a!.Type is JTokenType.Integer or JTokenType.Float;
        var bNum = b!.Type is JTokenType.Integer or JTokenType.Float;
        if (aNum && bNum)
        {
            return a.ToObject<double>().CompareTo(b.ToObject<double>());
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Stable client-side ordering of rows by the parsed order terms (shared by the index/join gatherers).
    private static IEnumerable<JObject> OrderRows(List<JObject> rows, IReadOnlyList<OrderColumn> orderTerms)
    {
        // A random term sorts by a random key drawn once per row, so the order is consistent within one sort.
        var randomKeys = orderTerms.Any(term => term.Column == SelectShape.RandomColumn)
            ? rows.Select(_ => Random.Shared.NextDouble()).ToArray()
            : null;

        return rows
            .Select((row, index) => (Row: row, Index: index))
            .OrderBy(x => x, System.Collections.Generic.Comparer<(JObject Row, int Index)>.Create((x, y) =>
            {
                foreach (var (column, desc) in orderTerms)
                {
                    var c = column == SelectShape.RandomColumn
                        ? randomKeys![x.Index].CompareTo(randomKeys[y.Index])
                        : CompareTokens(x.Row[column], y.Row[column]);
                    if (desc)
                    {
                        c = -c;
                    }

                    if (c != 0)
                    {
                        return c;
                    }
                }

                return x.Index.CompareTo(y.Index);
            }))
            .Select(x => x.Row);
    }

    // The ORDER BY and OFFSET/LIMIT clauses to run in Cosmos. A random order cannot be expressed in Cosmos, so the
    // rows are fetched unordered and unpaged and ordered and paged in the client.
    private static string OrderAndPagingClause(SelectShape shape)
        => shape.HasRandomOrder ? string.Empty : OrderByClause(shape.Order) + OffsetLimitClause(shape);

    // Applies the statement's OFFSET and LIMIT to rows that are already in their final order.
    private static IEnumerable<JObject> PageOfRows(IEnumerable<JObject> rows, SelectShape shape)
    {
        var page = rows.Skip(ClampToInt(shape.Offset));
        return shape.Limit is { } limit ? page.Take(ClampToInt(limit)) : page;
    }

    // "ORDER BY c["Col"] [DESC], ..." for the order columns, or nothing when there are none.
    internal static string OrderByClause(IEnumerable<OrderColumn> order)
    {
        var terms = order.Where(term => term.Column != SelectShape.RandomColumn)
            .Select(term => CosmosExpressionWriter.Property(term.Column) + (term.Descending ? " DESC" : string.Empty))
            .ToList();
        return terms.Count > 0 ? " ORDER BY " + string.Join(", ", terms) : string.Empty;
    }

    // Count the matching DocumentIds for a COUNT over a join (reduce / multi-index / single-index). Shared
    // by the scalar path (CountAsync) and the reader path (raw Inner/Left/Right join count API).
    private async Task<long> CountJoinAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        List<long> ids;
        if (shape.HasReduceJoin)
        {
            ids = await GatherReduceDocumentIdsAsync(shape, cancellationToken);
        }
        else if (shape.IndexJoins.Select(join => join.Table).Distinct().Count() >= 2)
        {
            ids = await GatherMultiIndexDocumentIdsAsync(shape, cancellationToken);
        }
        else
        {
            ids = await GatherDocumentIdsAsync(shape, cancellationToken);
        }

        return ids.Count;
    }

    // Count items in a partition: SELECT count(...) FROM [<table>] [WHERE <predicate>]. Shared by the
    // scalar path (CountAsync) and the reader path (raw Dapper QueryFirstOrDefaultAsync<int>).
    private async Task<long> CountItemsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var table = shape.RequiredFromTable;
        var cosmosWhere = shape.Where is null ? string.Empty : " AND " + await WriteWhereAsync(shape.Where, cancellationToken);

        var queryDef = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE " + Scoped(table) + cosmosWhere)
            .WithParameter("@pk", PkValue(table));
        queryDef = BindParameters(queryDef);

        using var iterator = CosmosContainer.GetItemQueryIterator<long>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) });
        while (iterator.HasMoreResults)
        {
            foreach (var n in await iterator.ReadNextAsync(cancellationToken))
            {
                return n;
            }
        }

        return 0L;
    }

    // Push paging into the Cosmos query (OFFSET ... LIMIT) so only the requested page is returned instead of every
    // matching item. Cosmos requires OFFSET and LIMIT together; ORDER BY is optional and appended separately.
    private static string OffsetLimitClause(SelectShape shape)
    {
        var offset = ClampToInt(shape.Offset);
        if (shape.Limit is { } limit)
        {
            return $" OFFSET {offset} LIMIT {ClampToInt(limit)}";
        }

        // Bare OFFSET with no LIMIT (e.g. .Skip(n) without .Take(...)): Cosmos rejects OFFSET on its own, so
        // pair it with a maximum LIMIT to skip the first n rows and return the rest.
        return offset > 0 ? $" OFFSET {offset} LIMIT {int.MaxValue}" : string.Empty;
    }

    // Query<T>() — all documents in the partition, optionally filtered by Type.
    private async Task<DbDataReader> QueryDocumentsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var docTable = shape.RequiredFromTable;

        // Type filter: YesSql usually binds @Type, but some callers (e.g. Orchard's QueriesDocument
        // migration) embed a [Type] = '<literal>' directly in the WHERE. Honour both, otherwise the
        // filter is silently dropped and the query returns the wrong document(s).
        object? typeFilter = TryParam("Type", out var typeVal) ? typeVal : null;
        typeFilter ??= SqlTree.TypeLiteral(shape.Where);

        var random = shape.HasRandomOrder;
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(docTable) + (typeFilter is not null ? " AND c.Type = @Type" : string.Empty) + OrderAndPagingClause(shape))
            .WithParameter("@pk", PkValue(docTable));
        if (typeFilter is not null)
        {
            queryDef = queryDef.WithParameter("@Type", typeFilter);
        }

        var items = new List<JObject>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(docTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                {
                    items.Add(item);
                }
            }
        }

        // Cosmos applied ORDER BY and OFFSET/LIMIT, so items is already the page, unless the order is random.
        IEnumerable<JObject> page = random ? PageOfRows(OrderRows(items, shape.Order), shape) : items;

        // Honour the SELECT projection. Dapper reads result columns positionally, so a single-column
        // projection (e.g. "SELECT [Content]") must return exactly that column — returning the full
        // document row would make Dapper read [Id] (a number) where [Content] (a string) was asked for.
        var columns = shape.Projection?.ToArray() ?? DocumentColumns;
        return new CosmosDbDataReader(columns, page.Select(item => ProjectRow(item, columns)).ToList());
    }

    // Project a document item onto the requested columns (numeric Id/Version as long, others as string).
    private static object?[] ProjectRow(JObject item, string[] columns)
    {
        var row = new object?[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            row[i] = columns[i] is "Id" or "Version" or "DocumentId"
                ? item[columns[i]]?.ToObject<long>()
                : item[columns[i]]?.ToObject<string>();
        }

        return row;
    }

    // Run a "SELECT DateTimePart(\"part\", [col]) FROM [table]" projection as a Cosmos VALUE query over the
    // partition, returning the computed integer(s) under a single column named after the part.
    private async Task<DbDataReader> ExecuteDatePartAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var (part, column) = shape.DatePart!.Value;
        if (part.Length == 0 || !part.All(char.IsAsciiLetter))
        {
            throw new SqlSyntaxException($"DateTimePart does not support the part \"{part}\"", CommandText);
        }

        var table = shape.RequiredFromTable;
        var cosmosWhere = shape.Where is null ? string.Empty : " AND " + await WriteWhereAsync(shape.Where, cancellationToken);

        var queryDef = new QueryDefinition($"SELECT VALUE DateTimePart(\"{part}\", {CosmosExpressionWriter.Property(column)}) FROM c WHERE " + Scoped(table) + cosmosWhere)
            .WithParameter("@pk", PkValue(table));
        queryDef = BindParameters(queryDef);

        var rows = new List<object?[]>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JToken>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var value in await iterator.ReadNextAsync(cancellationToken))
                {
                    rows.Add([value is null || value.Type == JTokenType.Null ? null : value.ToObject<long>()]);
                }
            }
        }

        return new CosmosDbDataReader([part], rows);
    }

    // Query<TIndex>() — return the index rows themselves (dynamic columns from the index fields).
    private async Task<DbDataReader> QueryIndexRowsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var indexTable = shape.RequiredFromTable;
        var cosmosWhere = shape.Where is null ? string.Empty : " AND " + await WriteWhereAsync(shape.Where, cancellationToken);

        var random = shape.HasRandomOrder;
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(indexTable) + cosmosWhere + OrderAndPagingClause(shape))
            .WithParameter("@pk", PkValue(indexTable));
        queryDef = BindParameters(queryDef);

        var all = new List<JObject>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(indexTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                {
                    all.Add(item);
                }
            }
        }

        // Cosmos applied ORDER BY and OFFSET/LIMIT, so all is already the page, unless the order is random.
        var items = random ? PageOfRows(OrderRows(all, shape.Order), shape).ToList() : all;
        var columns = new List<string>();
        foreach (var item in items)
        {
            foreach (var prop in item.Properties())
            {
                // Exclude the Cosmos envelope fields by exact (ordinal) name — the lowercase system "id",
                // "pk", and the "__table" discriminator — while keeping the index's own numeric "Id" column.
                if (!prop.Name.Equals("id", StringComparison.Ordinal)
                    && !prop.Name.Equals(PartitionKeyProperty, StringComparison.Ordinal)
                    && !prop.Name.Equals("__table", StringComparison.Ordinal)
                    && !columns.Contains(prop.Name))
                {
                    columns.Add(prop.Name);
                }
            }
        }

        var cols = columns.ToArray();
        var rows = items.Select(i => cols.Select(c => FromToken(i[c])).ToArray()).ToList();
        return new CosmosDbDataReader(cols, rows);
    }

    // Multi-index join across distinct index tables: query each index's DocumentId set (filtered by its
    // own aliases' predicates) and intersect them.
    private async Task<List<long>> GatherMultiIndexDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var joins = shape.IndexJoins;
        var terms = SqlTree.Conjuncts(SqlTree.WithoutDocumentTypePredicate(shape.Where));

        List<long>? result = null;
        foreach (var group in joins.GroupBy(j => j.Table))
        {
            var aliases = group.Select(j => j.Alias).ToList();
            var tableTerms = terms.Where(t => SqlTree.Qualifiers(t).Overlaps(aliases)).ToList();
            var sub = tableTerms.Count > 0 ? " AND " + await WriteWhereAsync(SqlTree.And(tableTerms)!, cancellationToken) : string.Empty;

            var queryDef = new QueryDefinition("SELECT VALUE c.DocumentId FROM c WHERE " + Scoped(group.Key) + sub).WithParameter("@pk", PkValue(group.Key));
            queryDef = BindParameters(queryDef);

            var ids = new HashSet<long>();
            using (var iterator = CosmosContainer.GetItemQueryIterator<long>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(group.Key) }))
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var v in await iterator.ReadNextAsync(cancellationToken))
                    {
                        ids.Add(v);
                    }
                }
            }

            result = result is null ? ids.ToList() : result.Where(ids.Contains).ToList();
        }

        var documentIds = (result ?? new List<long>()).Distinct().ToList();

        // Order across the joined indexes (Cosmos can't ORDER BY case-insensitively). The order column(s)
        // live in one of the joined index tables; gather their values per DocumentId, then sort client-side.
        var orderTerms = shape.Order;
        if (orderTerms.Count > 0 && documentIds.Count > 0)
        {
            var orderCols = orderTerms.Select(t => t.Column).Distinct()
                .Where(col => col != SelectShape.RandomColumn && !col.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)).ToList();
            var randomKeys = documentIds.ToDictionary(id => id, _ => Random.Shared.NextDouble());
            var orderValues = new Dictionary<long, JObject>();
            foreach (var group in joins.GroupBy(j => j.Table))
            {
                var projection = "c.DocumentId" + string.Concat(orderCols.Select(col => ", " + CosmosExpressionWriter.Property(col)));
                var orderQuery = new QueryDefinition("SELECT " + projection + " FROM c WHERE " + Scoped(group.Key) + " AND ARRAY_CONTAINS(@__ids, c.DocumentId)")
                    .WithParameter("@pk", PkValue(group.Key))
                    .WithParameter("@__ids", documentIds);
                using var iterator = CosmosContainer.GetItemQueryIterator<JObject>(orderQuery,
                    requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(group.Key) });
                while (iterator.HasMoreResults)
                {
                    foreach (var row in await iterator.ReadNextAsync(cancellationToken))
                    {
                        var docId = row["DocumentId"]!.ToObject<long>();
                        if (!orderValues.TryGetValue(docId, out var aggregate))
                        {
                            aggregate = new JObject();
                            orderValues[docId] = aggregate;
                        }

                        foreach (var col in orderCols)
                        {
                            if (aggregate[col] is null && row[col] is { } v && v.Type != JTokenType.Null)
                            {
                                aggregate[col] = v;
                            }
                        }
                    }
                }
            }

            documentIds = documentIds
                .Select((id, index) => (Id: id, Index: index))
                .OrderBy(x => x, System.Collections.Generic.Comparer<(long Id, int Index)>.Create((x, y) =>
                {
                    orderValues.TryGetValue(x.Id, out var xv);
                    orderValues.TryGetValue(y.Id, out var yv);
                    foreach (var (column, desc) in orderTerms)
                    {
                        var c = column == SelectShape.RandomColumn
                            ? randomKeys[x.Id].CompareTo(randomKeys[y.Id])
                            : column.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)
                                ? x.Id.CompareTo(y.Id)
                                : CompareTokens(xv?[column], yv?[column]);
                        if (desc)
                        {
                            c = -c;
                        }

                        if (c != 0)
                        {
                            return c;
                        }
                    }

                    return x.Index.CompareTo(y.Index);
                }))
                .Select(x => x.Id)
                .ToList();
        }

        return documentIds;
    }

    // Reduce-index query: doc ← bridge → index. Resolve in three steps — matching index Ids, then the
    // bridge rows linking them to documents, then the document ids.
    private async Task<List<long>> GatherReduceDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        // index↔bridge join: "JOIN [Index] AS idx ON idx.[Id] = <bridgeAlias>.[<FK>]". The bridge alias picks the RIGHT
        // bridge — a query may also join plain map indexes (.With<Map>()) whose "[DocumentId] = [Document].[Id]" join
        // looks identical to the reduce bridge's.
        var reduce = shape.Reduce;
        if (reduce?.BridgeTable is null)
        {
            throw new NotSupportedException($"Unsupported reduce query: {CommandText}");
        }

        var indexTable = reduce.IndexTable;
        var bridgeAlias = reduce.BridgeAlias;
        var bridgeForeignKey = reduce.BridgeColumn;
        var bridgeTable = reduce.BridgeTable;

        var indexWhere = string.Empty;
        var predicate = SqlTree.WithoutDocumentTypePredicate(shape.Where);
        if (predicate is not null)
        {
            indexWhere = " AND " + await WriteWhereAsync(predicate, cancellationToken);
        }

        // 1. matching index rows, with the columns the query orders by. The order columns belong to the reduce
        // index, so documents are ordered by the index row they belong to.
        // A term for the id of the document (YesSql adds one so that paging is stable) is not a column of the index. It
        // orders the documents that the index rows before it leave tied, so the terms after it never apply.
        var allTerms = shape.Order;
        var documentIdAt = allTerms.ToList().FindIndex(term => term.Column.Equals("DocumentId", StringComparison.OrdinalIgnoreCase));
        var orderTerms = documentIdAt < 0 ? allTerms : allTerms.Take(documentIdAt).ToList();
        var orderColumns = orderTerms.Select(t => t.Column)
            .Where(col => col != SelectShape.RandomColumn && !col.Equals("Id", StringComparison.OrdinalIgnoreCase)
                && !col.Equals("DocumentId", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        var indexProjection = "c.Id" + string.Concat(orderColumns.Select(col => ", " + CosmosExpressionWriter.Property(col)));
        var indexQuery = new QueryDefinition("SELECT " + indexProjection + " FROM c WHERE " + Scoped(indexTable) + indexWhere).WithParameter("@pk", PkValue(indexTable));
        indexQuery = BindParameters(indexQuery);

        var indexRows = new List<JObject>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(indexQuery,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(indexTable) }))
        {
            while (iterator.HasMoreResults)
            {
                indexRows.AddRange(await iterator.ReadNextAsync(cancellationToken));
            }
        }

        if (indexRows.Count == 0)
        {
            return new List<long>();
        }

        // The position of each index row in the requested order; without an ORDER BY the order is the query's.
        var indexIds = (orderTerms.Count == 0 ? indexRows : OrderRows(indexRows, orderTerms))
            .Select(row => row["Id"]!.ToObject<long>()).ToList();
        var indexPosition = new Dictionary<long, int>();
        for (var i = 0; i < indexIds.Count; i++)
        {
            indexPosition[indexIds[i]] = i;
        }

        // 2. bridge rows linking those index rows to documents
        var bridgeQuery = new QueryDefinition(
            $"SELECT c.DocumentId, c[\"{bridgeForeignKey}\"] AS IndexId FROM c WHERE " + Scoped(bridgeTable) + $" AND ARRAY_CONTAINS(@__indexIds, c[\"{bridgeForeignKey}\"])")
            .WithParameter("@pk", PkValue(bridgeTable))
            .WithParameter("@__indexIds", indexIds);

        var bridgeRows = new List<(long DocumentId, int Position)>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(bridgeQuery,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(bridgeTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var row in await iterator.ReadNextAsync(cancellationToken))
                {
                    bridgeRows.Add((row["DocumentId"]!.ToObject<long>(), indexPosition[row["IndexId"]!.ToObject<long>()]));
                }
            }
        }

        // Documents follow their index row's position, and a document in several index rows takes the first. Documents
        // at the same position (all of them, when the only order is the document id) are ordered by their id.
        var firstPosition = new Dictionary<long, int>();
        foreach (var (documentId, position) in bridgeRows)
        {
            if (!firstPosition.TryGetValue(documentId, out var known) || position < known)
            {
                firstPosition[documentId] = position;
            }
        }

        // Ties are broken by document id, ascending unless the query asked for it descending, so that the same query
        // always returns the same order and pages never repeat or skip a document. With no index column before the
        // document id, the index rows give no order, so the position is not used.
        var idDescending = documentIdAt >= 0 && allTerms[documentIdAt].Descending;
        var byIndexOrder = documentIdAt < 0 || orderTerms.Count > 0;
        IEnumerable<KeyValuePair<long, int>> placed;
        if (byIndexOrder)
        {
            var byPosition = firstPosition.OrderBy(pair => pair.Value);
            placed = idDescending ? byPosition.ThenByDescending(pair => pair.Key) : byPosition.ThenBy(pair => pair.Key);
        }
        else
        {
            placed = idDescending ? firstPosition.OrderByDescending(pair => pair.Key) : firstPosition.OrderBy(pair => pair.Key);
        }

        var documentIds = placed.Select(pair => pair.Key).ToList();

        // A reduce query may also join plain map indexes (.With<Map>().With<Reduce>()). Intersect: keep only
        // documents that also have a row in each such map index (the bridge itself is excluded by alias).
        foreach (var (mapTable, mapAlias, _) in shape.IndexJoins)
        {
            if (documentIds.Count == 0 || mapAlias.Equals(bridgeAlias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mapIds = new HashSet<long>();
            var mapQuery = new QueryDefinition("SELECT VALUE c.DocumentId FROM c WHERE " + Scoped(mapTable) + " AND ARRAY_CONTAINS(@__ids, c.DocumentId)")
                .WithParameter("@pk", PkValue(mapTable))
                .WithParameter("@__ids", documentIds);
            using var mapIterator = CosmosContainer.GetItemQueryIterator<long>(mapQuery,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(mapTable) });
            while (mapIterator.HasMoreResults)
            {
                foreach (var v in await mapIterator.ReadNextAsync(cancellationToken))
                {
                    mapIds.Add(v);
                }
            }

            documentIds = documentIds.Where(mapIds.Contains).ToList();
        }

        return documentIds;
    }

    // Monotonic, never-reused id allocator for index rows (auto-increment has no Cosmos equivalent, and
    // MAX+1 reuses ids after deletes — which breaks YesSql's append-only index expectations). A counter
    // doc per table lives in an isolated "__seq" partition so it never appears in index/count queries.
    private async Task<long> NextSequenceAsync(string table, CancellationToken cancellationToken)
    {
        var seqPk = new PartitionKey("__seq");

        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (attempt > 0)
            {
                // Concurrent allocators for the same table collide on the counter's ETag; spread the retries.
                await Task.Delay(Random.Shared.Next(2, 10 * (attempt + 1)), cancellationToken);
            }

            try
            {
                var current = await CosmosContainer.ReadItemAsync<JObject>(table, seqPk, cancellationToken: cancellationToken);
                var next = (current.Resource["next"]?.ToObject<long>() ?? 0) + 1;
                current.Resource["next"] = next;
                await CosmosContainer.ReplaceItemAsync(current.Resource, table, seqPk,
                    new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
                return next;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                var seed = (await MaxIdAsync(table, cancellationToken) ?? 0) + 1;
                try
                {
                    await CosmosContainer.CreateItemAsync(new JObject { ["id"] = table, [PartitionKeyProperty] = "__seq", ["next"] = seed }, seqPk, cancellationToken: cancellationToken);
                    return seed;
                }
                catch (CosmosException dup) when (dup.StatusCode == HttpStatusCode.Conflict)
                {
                    // created concurrently — retry the read/increment path
                }
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // lost the ETag race — retry
            }
        }

        throw new InvalidOperationException($"Could not allocate a sequence id for '{table}'.");
    }

    private async Task<long?> MaxIdAsync(string table, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT VALUE MAX(c.Id) FROM c WHERE " + Scoped(table)).WithParameter("@pk", PkValue(table));
        using var iterator = CosmosContainer.GetItemQueryIterator<long?>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) });

        while (iterator.HasMoreResults)
        {
            foreach (var v in await iterator.ReadNextAsync(cancellationToken))
            {
                return v;
            }
        }

        return null;
    }

    private static object?[] ToRow(JObject item) =>
    [
        item["Id"]?.ToObject<long>(),
        item["Type"]?.ToObject<string>(),
        item["Content"]?.ToObject<string>(),
        item["Version"]?.ToObject<long>(),
    ];

    private static JToken ToToken(object? value) => value switch
    {
        null => JValue.CreateNull(),
        // JSON has no binary type; wrap byte[] self-descriptively so reads can recover it as byte[]
        // (a bare base64 string would come back as a string and fail the byte[] cast).
        byte[] bytes => new JObject { ["$b64"] = Convert.ToBase64String(bytes) },
        _ => JToken.FromObject(AsUtc(value)!),
    };

    // A moment in time is stored and queried as a UTC instant ("...Z"), never with an offset. Cosmos DB compares
    // DateTimeToTimestamp(c.x) wrongly in a WHERE clause for a stored offset east of +01:00 ("...+05:30" is
    // never equal to its own instant), so a value written with such an offset could not be found by a query.
    // A DateTime of unspecified kind is left as it is, because it carries no offset.
    private static object? AsUtc(object? value) => value switch
    {
        DateTimeOffset moment => moment.UtcDateTime,
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        _ => value,
    };

    // Reverse of ToToken for reading column values: recover wrapped byte[]; otherwise the raw CLR value.
    private static object? FromToken(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token is JObject obj && obj["$b64"] is { } b64)
        {
            return Convert.FromBase64String(b64.Value<string>()!);
        }

        return token.ToObject<object>();
    }

    private object? Param(string name)
        => TryParam(name, out var value) ? value : throw new InvalidOperationException($"Parameter '{name}' not found for: {CommandText}");

    private bool TryParam(string name, out object? value)
    {
        foreach (DbParameter p in _parameters)
        {
            if (string.Equals(p.ParameterName.TrimStart('@'), name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value is DBNull ? null : p.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
