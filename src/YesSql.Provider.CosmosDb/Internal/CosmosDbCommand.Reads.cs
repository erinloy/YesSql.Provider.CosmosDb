using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

// Queries that return documents or index rows: loading by key, the document table, an index table, and the joins of a
// document to one map index, several map indexes or a reduce index. Ordering and paging are done here when Cosmos
// cannot do them.
internal sealed partial class CosmosDbCommand
{
    // Ids sent to Cosmos in one query that only filters, which return ids and not documents.
    private const int IdsPerFilterQuery = 5000;

    // Runs a query inside one partition and returns everything it yields.
    private async Task<List<T>> ReadAllAsync<T>(QueryDefinition query, string table, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        using var iterator = CosmosContainer.GetItemQueryIterator<T>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) });
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    // Runs a query that filters by a list of ids, as ARRAY_CONTAINS(@__ids, ...). The ids travel as one query parameter,
    // so a long list is split over several queries instead of making one that Cosmos would refuse.
    private async Task<List<T>> ReadByIdsAsync<T>(IReadOnlyCollection<long> ids, string table, Func<long[], QueryDefinition> query, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        foreach (var chunk in ids.Chunk(IdsPerFilterQuery))
        {
            results.AddRange(await ReadAllAsync<T>(query(chunk), table, cancellationToken));
        }

        return results;
    }

    // The ids a load-by-key query asks for. Only [Id] = x and [Id] IN (x, ...) are understood, and anything else is refused
    // because the ids are all the provider reads from such a query.
    private List<long> KeysOf(SqlExpr? predicate)
    {
        var operands = SqlTree.KeyOperands(predicate)
            ?? throw new NotSupportedException($"A query by key supports only [Id] = x and [Id] IN (x, ...): {CommandText}");
        var keys = new List<long>();
        foreach (var operand in operands)
        {
            object? value = operand switch
            {
                ParamRef parameter => Param(parameter.Name),
                LiteralExpr { Value: long number } => number,
                _ => null,
            };
            if (value is not null)
            {
                keys.Add(Convert.ToInt64(value));
            }
        }

        return keys;
    }

    // The documents with the given ids, on the requested page of them, as the result of a query over the document table.
    private async Task<DbDataReader> DocumentsOfAsync(SelectShape shape, List<long> documentIds, CancellationToken cancellationToken)
    {
        var rows = await ReadDocumentRowsAsync(shape.RequiredFromTable, PageOf(documentIds, shape), cancellationToken);
        return new CosmosDbDataReader(DocumentColumns, rows);
    }

    // Ids read by one query when loading documents, and queries in flight at once.
    private const int DocumentsPerQuery = 100;
    private const int QueryConcurrency = 4;

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

    // Reads documents by id as result rows, in the order of ids; ids with no document are skipped. One id is a point
    // read. Several are read with one query per DocumentsPerQuery ids, which costs one round trip where point reads
    // cost one each (and at about 60 ms to a real account, 20 point reads take longer than one query even when eight
    // run at once).
    private async Task<List<object?[]>> ReadDocumentRowsAsync(string table, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new List<object?[]>();
        }

        var byId = new Dictionary<long, JObject>();
        if (ids.Count == 1)
        {
            try
            {
                var response = await CosmosContainer.ReadItemAsync<JObject>($"{table}:{ids[0]}", PartitionKeyFor(table), cancellationToken: cancellationToken);
                byId[ids[0]] = response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // no document for this id
            }
        }
        else
        {
            var chunks = ids.Distinct().Chunk(DocumentsPerQuery).ToList();
            using var gate = new SemaphoreSlim(QueryConcurrency);
            await Task.WhenAll(chunks.Select(async chunk =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var query = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(table) + " AND ARRAY_CONTAINS(@__docIds, c.Id)")
                        .WithParameter("@pk", PkValue(table))
                        .WithParameter("@__docIds", chunk);
                    using var iterator = CosmosContainer.GetItemQueryIterator<JObject>(query,
                        requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) });
                    while (iterator.HasMoreResults)
                    {
                        foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                        {
                            lock (byId)
                            {
                                byId[item["Id"]!.ToObject<long>()] = item;
                            }
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        return ids.Where(byId.ContainsKey).Select(id => ToRow(byId[id])).ToList();
    }

    // Runs the index lookup of a query over one map index and returns the distinct DocumentIds, in the query's order when
    // it has an ORDER BY. Shared by the reader, which then loads the documents, and by counts.
    private async Task<List<long>> GatherDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        // Accept both the .With() form ("… = [Document].[Id]") and the raw SqlBuilder join form ("… = d.[Id]", aliased).
        var join = shape.LinkJoin ?? throw new NotSupportedException($"Unsupported join query: {CommandText}");
        var indexTable = join.Table;

        // The conditions are on the index rows. The document-Type condition YesSql adds is not, so it is taken out here and
        // applied to the documents afterwards.
        var predicate = SqlTree.WithoutDocumentTypePredicate(shape.Where);
        var cosmosWhere = predicate is null ? string.Empty : " AND " + await WriteWhereAsync(predicate, cancellationToken);

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

        var queryDef = new QueryDefinition("SELECT " + projection + " FROM c WHERE " + Scoped(indexTable) + cosmosWhere)
            .WithParameter("@pk", PkValue(indexTable));
        queryDef = BindParameters(queryDef);

        var documentIds = new List<long>();
        var seenDocumentIds = new HashSet<long>();
        if (orderTerms.Count == 0)
        {
            foreach (var documentId in await ReadAllAsync<long>(queryDef, indexTable, cancellationToken))
            {
                if (seenDocumentIds.Add(documentId))
                {
                    documentIds.Add(documentId);
                }
            }
        }
        else
        {
            var rows = await ReadAllAsync<JObject>(queryDef, indexTable, cancellationToken);
            foreach (var row in OrderRows(rows, orderTerms))
            {
                var documentId = row["DocumentId"]!.ToObject<long>();
                if (seenDocumentIds.Add(documentId))
                {
                    documentIds.Add(documentId);
                }
            }
        }

        return await FilterByDocumentTypeAsync(shape, documentIds, cancellationToken);
    }

    // filterType:true adds a "[Document].[Type] = @p" condition that cannot run inside the index partition, so it was taken
    // out of the index query. This applies it: only the ids whose document has that exact Type are kept, in their order.
    // Without it a Query<SubClass>(filterType:true) would count every subclass, not just one.
    private async Task<List<long>> FilterByDocumentTypeAsync(SelectShape shape, List<long> documentIds, CancellationToken cancellationToken)
    {
        var typeParameter = SqlTree.DocumentTypeParameter(shape.Where);
        if (typeParameter is null || documentIds.Count == 0)
        {
            return documentIds;
        }

        var typeValue = Param(typeParameter);
        var documentTable = shape.RequiredFromTable;
        var matching = (await ReadByIdsAsync<long>(documentIds, documentTable, ids =>
                new QueryDefinition("SELECT VALUE c.Id FROM c WHERE " + Scoped(documentTable) + " AND c.Type = @__type AND ARRAY_CONTAINS(@__ids, c.Id)")
                    .WithParameter("@pk", PkValue(documentTable))
                    .WithParameter("@__type", typeValue)
                    .WithParameter("@__ids", ids), cancellationToken))
            .ToHashSet();

        return documentIds.Where(matching.Contains).ToList();
    }

    // Order comparison matching the reference dialects: nulls first, numbers numerically, dates as moments in time,
    // everything else as a case-insensitive string.
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

        // Cosmos returns a date column's text, and the JSON reader turns it into a date. Its ToString() is the culture's
        // text without the fractions of a second, which ties moments within one second and sorts 9:59 AM after 10:00 AM.
        if (a.Type == JTokenType.Date && b.Type == JTokenType.Date)
        {
            return MomentOf(a).CompareTo(MomentOf(b));
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // The instant a date token stands for. Dates are stored as UTC ("...Z"); a value written with an offset by an earlier
    // version is read as a local time, which is converted. A date with no kind is compared as written.
    private static DateTime MomentOf(JToken token)
    {
        var value = ((JValue)token).Value;
        var moment = value is DateTimeOffset offset ? offset.UtcDateTime : (DateTime)value!;
        return moment.Kind == DateTimeKind.Local ? moment.ToUniversalTime() : moment;
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

    // Query<T>(), all documents in the partition, optionally filtered by Type.
    private async Task<DbDataReader> QueryDocumentsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var docTable = shape.RequiredFromTable;

        // The type of the documents is the one condition this query reads, as a parameter or, in some callers (for example
        // Orchard's QueriesDocument migration), as a literal. Any other condition would be dropped silently, so it is refused.
        if (SqlTree.WithoutTypeComparison(shape.Where) is not null)
        {
            throw new NotSupportedException($"A query over a document table supports only a filter on its Type: {CommandText}");
        }

        var typeValue = SqlTree.TypeComparison(shape.Where) switch
        {
            ParamRef parameter => (HasFilter: true, Value: Param(parameter.Name)),
            LiteralExpr { Value: string text } => (HasFilter: true, Value: (object?)text),
            _ => (HasFilter: false, Value: null),
        };

        var random = shape.HasRandomOrder;
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(docTable) + (typeValue.HasFilter ? " AND c.Type = @Type" : string.Empty) + OrderAndPagingClause(shape))
            .WithParameter("@pk", PkValue(docTable));
        if (typeValue.HasFilter)
        {
            queryDef = queryDef.WithParameter("@Type", typeValue.Value);
        }

        var items = await ReadAllAsync<JObject>(queryDef, docTable, cancellationToken);

        // Cosmos applied ORDER BY and OFFSET/LIMIT, so items is already the page, unless the order is random.
        IEnumerable<JObject> page = random ? PageOfRows(OrderRows(items, shape.Order), shape) : items;

        // Honour the SELECT projection. Dapper reads result columns positionally, so a single-column
        // projection (e.g. "SELECT [Content]") must return exactly that column, returning the full
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

        var values = await ReadAllAsync<JToken>(queryDef, table, cancellationToken);
        var rows = values.Select(value => new object?[] { value is null || value.Type == JTokenType.Null ? null : value.ToObject<long>() }).ToList();

        return new CosmosDbDataReader([part], rows);
    }

    // Query<TIndex>(), return the index rows themselves (dynamic columns from the index fields).
    private async Task<DbDataReader> QueryIndexRowsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var indexTable = shape.RequiredFromTable;
        var cosmosWhere = shape.Where is null ? string.Empty : " AND " + await WriteWhereAsync(shape.Where, cancellationToken);

        var random = shape.HasRandomOrder;
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(indexTable) + cosmosWhere + OrderAndPagingClause(shape))
            .WithParameter("@pk", PkValue(indexTable));
        queryDef = BindParameters(queryDef);

        var all = await ReadAllAsync<JObject>(queryDef, indexTable, cancellationToken);

        // Cosmos applied ORDER BY and OFFSET/LIMIT, so all is already the page, unless the order is random.
        var items = random ? PageOfRows(OrderRows(all, shape.Order), shape).ToList() : all;
        var columns = new List<string>();
        foreach (var item in items)
        {
            foreach (var prop in item.Properties())
            {
                // Exclude the Cosmos envelope fields by exact (ordinal) name, the lowercase system "id",
                // "pk", and the "__table" discriminator, while keeping the index's own numeric "Id" column.
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

    // The DocumentIds of an index table whose rows meet the terms, which refer to that table only.
    private async Task<List<long>> DocumentIdsOfIndexAsync(string indexTable, IReadOnlyList<SqlExpr> terms, CancellationToken cancellationToken)
    {
        var where = terms.Count > 0 ? " AND " + await WriteWhereAsync(SqlTree.And(terms)!, cancellationToken) : string.Empty;
        var query = BindParameters(new QueryDefinition("SELECT VALUE c.DocumentId FROM c WHERE " + Scoped(indexTable) + where)
            .WithParameter("@pk", PkValue(indexTable)));
        return await ReadAllAsync<long>(query, indexTable, cancellationToken);
    }

    // Multi-index join across distinct index tables: query each index's DocumentId set (filtered by the conditions on its own
    // aliases) and intersect them. A condition has to refer to one index table, see SqlTree.SplitByTable.
    private async Task<List<long>> GatherMultiIndexDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var joins = shape.IndexJoins;
        var termsOf = SqlTree.SplitByTable(SqlTree.WithoutDocumentTypePredicate(shape.Where), joins.ToDictionary(join => join.Alias, join => join.Table), CommandText);

        List<long>? result = null;
        foreach (var group in joins.GroupBy(join => join.Table))
        {
            var ids = (await DocumentIdsOfIndexAsync(group.Key, termsOf[group.Key], cancellationToken)).ToHashSet();
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
            foreach (var group in joins.GroupBy(join => join.Table))
            {
                var projection = "c.DocumentId" + string.Concat(orderCols.Select(col => ", " + CosmosExpressionWriter.Property(col)));
                var rows = await ReadByIdsAsync<JObject>(documentIds, group.Key, ids =>
                    new QueryDefinition("SELECT " + projection + " FROM c WHERE " + Scoped(group.Key) + " AND ARRAY_CONTAINS(@__ids, c.DocumentId)")
                        .WithParameter("@pk", PkValue(group.Key))
                        .WithParameter("@__ids", ids), cancellationToken);
                foreach (var row in rows)
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

            documentIds = documentIds
                .Select((id, index) => (Id: id, Index: index))
                .OrderBy(x => x, Comparer<(long Id, int Index)>.Create((x, y) =>
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

        return await FilterByDocumentTypeAsync(shape, documentIds, cancellationToken);
    }

    // Reduce index query: the document is linked to the index by a bridge table. Resolve it in three steps: the matching index ids, then the
    // bridge rows linking them to documents, then the document ids.
    private async Task<List<long>> GatherReduceDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        // index to bridge join: "JOIN [Index] AS idx ON idx.[Id] = <bridgeAlias>.[<FK>]". The bridge alias picks the RIGHT
        // bridge, a query may also join plain map indexes (.With<Map>()) whose "[DocumentId] = [Document].[Id]" join
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

        // A reduce query may also join plain map indexes (.With<Map>().With<Reduce>()), and a query that combines several
        // conditions on the reduce index joins it once for each. Each condition belongs to the reduce index or to one of the
        // map indexes, see SqlTree.SplitByTable. The bridge tables are joined to find the documents, and conditions on them
        // are not supported.
        var mapJoins = shape.IndexJoins.Where(join => !join.Alias.Equals(bridgeAlias, StringComparison.OrdinalIgnoreCase)).ToList();
        var tables = shape.NamedJoins.Where(join => join.Table.Equals(indexTable, StringComparison.OrdinalIgnoreCase)).ToDictionary(join => join.Alias, join => join.Table);
        foreach (var join in mapJoins.Where(join => !join.Table.Equals(bridgeTable, StringComparison.OrdinalIgnoreCase)))
        {
            tables[join.Alias] = join.Table;
        }

        var termsOf = SqlTree.SplitByTable(SqlTree.WithoutDocumentTypePredicate(shape.Where), tables, CommandText);
        var indexTerms = termsOf[indexTable];
        var indexWhere = indexTerms.Count > 0 ? " AND " + await WriteWhereAsync(SqlTree.And(indexTerms)!, cancellationToken) : string.Empty;

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
        var indexQuery = BindParameters(new QueryDefinition("SELECT " + indexProjection + " FROM c WHERE " + Scoped(indexTable) + indexWhere)
            .WithParameter("@pk", PkValue(indexTable)));

        var indexRows = await ReadAllAsync<JObject>(indexQuery, indexTable, cancellationToken);
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
        var foreignKey = CosmosExpressionWriter.Property(bridgeForeignKey);
        var bridgeRows = (await ReadByIdsAsync<JObject>(indexIds, bridgeTable, ids =>
                new QueryDefinition($"SELECT c.DocumentId, {foreignKey} AS IndexId FROM c WHERE " + Scoped(bridgeTable) + $" AND ARRAY_CONTAINS(@__indexIds, {foreignKey})")
                    .WithParameter("@pk", PkValue(bridgeTable))
                    .WithParameter("@__indexIds", ids), cancellationToken))
            .Select(row => (DocumentId: row["DocumentId"]!.ToObject<long>(), Position: indexPosition[row["IndexId"]!.ToObject<long>()]))
            .ToList();

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

        // Keep only the documents that also have a row in each map index that meets its conditions.
        foreach (var group in mapJoins.GroupBy(join => join.Table))
        {
            if (documentIds.Count == 0)
            {
                break;
            }

            var terms = termsOf.TryGetValue(group.Key, out var found) ? found : new List<SqlExpr>();
            var extra = terms.Count > 0 ? " AND " + await WriteWhereAsync(SqlTree.And(terms)!, cancellationToken) : string.Empty;
            var mapIds = (await ReadByIdsAsync<long>(documentIds, group.Key, ids =>
                    BindParameters(new QueryDefinition("SELECT VALUE c.DocumentId FROM c WHERE " + Scoped(group.Key) + " AND ARRAY_CONTAINS(@__ids, c.DocumentId)" + extra)
                        .WithParameter("@pk", PkValue(group.Key))
                        .WithParameter("@__ids", ids)), cancellationToken))
                .ToHashSet();

            documentIds = documentIds.Where(mapIds.Contains).ToList();
        }

        return await FilterByDocumentTypeAsync(shape, documentIds, cancellationToken);
    }

}
