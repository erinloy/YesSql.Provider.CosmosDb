using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbCommand"/> shim that translates the bounded SQL surface YesSql emits into Cosmos
/// SDK operations. Statements are dispatched on their leading keyword; values are read from the
/// <see cref="DbParameterCollection"/> (Id/Type/Content/Version) rather than by parsing clauses.
/// </summary>
/// <remarks>
/// Document storage model (single container, type-discriminated): each YesSql document table row becomes
/// a Cosmos item <c>{ id: "&lt;table&gt;:&lt;Id&gt;", pk: "&lt;table&gt;", Id, Type, Content, Version }</c>.
/// The partition key is the table name so a unit of work stays within one logical partition.
/// </remarks>
public sealed class CosmosDbCommand : DbCommand
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

    // The active unit of work's undo log (set by YesSql on the command), or null when untracked.
    private CosmosDbTransaction? Undo => DbTransaction as CosmosDbTransaction;

    // WHERE fragment that scopes a query to one table's items (bind the named param to PkValue(table)).
    // In PerStore the single partition holds every table, so the __table discriminator is required.
    private string Scoped(string table, string pkParam = "@pk")
        => _connection.Options.PartitionStrategy == PartitionStrategy.PerStore
            ? $"c.pk = {pkParam} AND c.__table = \"{table}\""
            : $"c.pk = {pkParam}";

    // Stamp the partition key + table discriminator onto an item being written.
    private JObject WithPartition(JObject item, string table)
    {
        item["pk"] = PkValue(table);
        item["__table"] = table;
        return item;
    }

    // ---- async (primary) path, used by Dapper via QueryAsync/ExecuteAsync ----

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var sql = CommandText.TrimStart();

        // RenameColumn DDL (emitted by the schema interpreter): rewrite the field on every row in the
        // partition. Cosmos is schemaless, so a column rename is a data rewrite, not metadata.
        if (StartsWith(sql, "renamecolumn"))
        {
            var rename = Regex.Match(sql, @"renamecolumn\s+\[([^\]]+)\]\s+\[([^\]]+)\]\s+\[([^\]]+)\]", RegexOptions.IgnoreCase);
            if (!rename.Success)
            {
                return 0;
            }

            var renameTable = rename.Groups[1].Value;
            var oldColumn = rename.Groups[2].Value;
            var newColumn = rename.Groups[3].Value;

            var renameQuery = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(renameTable)).WithParameter("@pk", PkValue(renameTable));
            var renamed = 0;
            using var renameIterator = CosmosContainer.GetItemQueryIterator<JObject>(renameQuery,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(renameTable) });
            while (renameIterator.HasMoreResults)
            {
                foreach (var item in await renameIterator.ReadNextAsync(cancellationToken))
                {
                    if (item.Property(oldColumn) is null)
                    {
                        continue;
                    }

                    item[newColumn] = item[oldColumn];
                    item.Remove(oldColumn);
                    await CosmosContainer.UpsertItemAsync(item, PartitionKeyFor(renameTable), cancellationToken: cancellationToken);
                    renamed++;
                }
            }

            return renamed;
        }

        // Reduce-index bridge row (Index↔Document link). The columns (e.g. [ArticlesByDayId],
        // [DocumentId]) don't match the param names (@Id, @DocumentId), so map columns→params by
        // position. Composite key (<indexFk>:<documentId>) — many rows share an index Id.
        if (StartsWith(sql, "insert") && TryParam("DocumentId", out var bridgeDocId) && !TryParam("Type", out _) && !TryParam("Content", out _))
        {
            var bridgeTable = ExtractTable(sql);
            var cv = Regex.Match(sql, @"\(([^)]*)\)\s*values\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
            var columns = cv.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('[', ']')).ToArray();
            var values = cv.Groups[2].Value.Split(',').Select(v => v.Trim().TrimStart('@')).ToArray();

            var bridge = new JObject();
            for (var i = 0; i < columns.Length && i < values.Length; i++)
            {
                bridge[columns[i]] = ToToken(TryParam(values[i], out var pv) ? pv : null);
            }

            var indexFk = columns.Length > 0 ? bridge[columns[0]]?.ToString() : "0";
            bridge["id"] = $"{bridgeTable}:{indexFk}:{bridgeDocId}";
            WithPartition(bridge, bridgeTable);

            await CosmosContainer.UpsertItemAsync(bridge, PartitionKeyFor(bridgeTable), cancellationToken: cancellationToken);
            Undo?.Record(bridge["id"]!.ToString(), PkValue(bridgeTable), null);
            return 1;
        }

        if (StartsWith(sql, "insert") || StartsWith(sql, "update"))
        {
            var table = ExtractTable(sql);

            // UPDATE only carries the columns in its SET clause (Content/Version), so read the existing
            // item and patch the provided fields; INSERT carries all of them.
            var isUpdate = StartsWith(sql, "update");

            // Resolve the row Id. UPDATE always carries @Id (its key). An INSERT into an identity table
            // (e.g. Orchard's [RecordIndexingTask]) carries no @Id — Cosmos has no auto-increment, so
            // allocate Id = next sequence, mirroring the scalar-insert path above.
            long id;
            if (TryParam("Id", out var idParam) && idParam is not null and not DBNull)
            {
                id = Convert.ToInt64(idParam);
            }
            else if (!isUpdate)
            {
                id = await NextSequenceAsync(table, cancellationToken);
            }
            else
            {
                id = Convert.ToInt64(Param("Id")); // UPDATE without its key — preserve the original error
            }
            JObject? item = null;
            string? etag = null;
            if (isUpdate)
            {
                try
                {
                    var existing = await CosmosContainer.ReadItemAsync<JObject>($"{table}:{id}", PartitionKeyFor(table), cancellationToken: cancellationToken);
                    item = existing.Resource;
                    etag = existing.ETag;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // fall through to a fresh item
                }
            }

            // Optimistic concurrency: a checked update adds "and [Version] = <n>" (or "IS NULL OR = <n>");
            // YesSql throws ConcurrencyException when the affected count is not 1, so return 0 on mismatch.
            var versionCheck = isUpdate ? Regex.Match(sql, @"\[version\]\s*=\s*(\d+)", RegexOptions.IgnoreCase) : Match.Empty;
            if (versionCheck.Success)
            {
                var checkVersion = long.Parse(versionCheck.Groups[1].Value);
                var allowNull = Regex.IsMatch(sql, @"\[version\]\s+is\s+null", RegexOptions.IgnoreCase);
                var current = item?["Version"];
                var currentVersion = current is null || current.Type == JTokenType.Null ? (long?)null : current.ToObject<long>();
                if (item is null || !(currentVersion == checkVersion || (allowNull && currentVersion is null)))
                {
                    return 0;
                }
            }

            // Snapshot the prior state (for rollback) before patching; null ⇒ this is an insert.
            var prior = item is null ? null : (JObject)item.DeepClone();

            item ??= new JObject { ["id"] = $"{table}:{id}", ["Id"] = id };

            // Patch every provided column (documents: Type/Content/Version; indexes: their own fields).
            // Id is the key and already set.
            foreach (DbParameter p in _parameters)
            {
                var name = p.ParameterName.TrimStart('@');
                if (!name.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    item[name] = ToToken(p.Value is DBNull ? null : p.Value);
                }
            }

            // INSERT with literal VALUES (no parameters), e.g. "INSERT INTO [T] ([C1]) VALUES ('v')" — map
            // each "[Column]" to its parsed literal. Parameterised positions (@p) are already handled above.
            if (!isUpdate)
            {
                var insertMatch = Regex.Match(sql, @"insert\s+into\s+\[[^\]]+\]\s*\(([^)]*)\)\s*values\s*\((.*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (insertMatch.Success)
                {
                    var insertCols = Regex.Matches(insertMatch.Groups[1].Value, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value).ToList();
                    var insertVals = SplitTopLevelCommas(insertMatch.Groups[2].Value);
                    for (var i = 0; i < insertCols.Count && i < insertVals.Count; i++)
                    {
                        var raw = insertVals[i].Trim();
                        if (raw.StartsWith('@') || insertCols[i].Equals("Id", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        item[insertCols[i]] = ParseSqlLiteral(raw);
                    }
                }
            }

            WithPartition(item, table);

            // Version-checked updates use an ETag-conditional replace so a concurrent write between the
            // read and the write is also detected (412 ⇒ treat as a concurrency failure).
            if (versionCheck.Success && etag is not null)
            {
                try
                {
                    await CosmosContainer.ReplaceItemAsync(item, $"{table}:{id}", PartitionKeyFor(table),
                        new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    return 0;
                }
            }
            else
            {
                await CosmosContainer.UpsertItemAsync(item, PartitionKeyFor(table), cancellationToken: cancellationToken);
            }

            Undo?.Record($"{table}:{id}", PkValue(table), prior);
            return 1;
        }

        if (StartsWith(sql, "delete"))
        {
            // General delete: query items in the partition matching the WHERE (by [Id] for documents,
            // by [DocumentId] for map indexes, by composite key for reduce bridge rows), then delete each.
            var table = ExtractTable(sql);
            var where = ExtractWhere(sql);
            var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

            // SELECT * (not just id) so the full items can be restored on rollback.
            var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(table) + cosmosWhere).WithParameter("@pk", PkValue(table));
            foreach (DbParameter p in _parameters)
            {
                queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
            }

            var items = new List<JObject>();
            using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) }))
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                    {
                        items.Add(item);
                    }
                }
            }

            foreach (var item in items)
            {
                var id = item["id"]!.ToString();
                Undo?.Record(id, PkValue(table), item); // restore the deleted item on rollback
                try
                {
                    await CosmosContainer.DeleteItemAsync<JObject>(id, PartitionKeyFor(table), cancellationToken: cancellationToken);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // already gone
                }
            }

            return items.Count;
        }

        throw new NotSupportedException($"Unsupported non-query statement: {CommandText}");
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var sql = CommandText;

        // DefaultIdGenerator seed: SELECT MAX([Id]) FROM [<table>]
        if (Regex.IsMatch(sql, @"max\s*\(", RegexOptions.IgnoreCase))
        {
            var table = ExtractTableAfter(sql, "from");
            return await MaxIdAsync(table, cancellationToken);
        }

        // CountAsync over an index join: SELECT count(distinct [Document].[Id]) FROM [Document] INNER
        // JOIN [Index] … WHERE … → count the matching DocumentIds.
        if (Regex.IsMatch(sql, @"count\s*\(", RegexOptions.IgnoreCase) && Regex.IsMatch(sql, @"\bjoin\b", RegexOptions.IgnoreCase))
        {
            return await CountJoinAsync(sql, cancellationToken);
        }

        // CountAsync without a join: SELECT count(*) FROM [<table>] [WHERE <predicate>] — count items in
        // that partition (documents by Type, or index rows).
        if (Regex.IsMatch(sql, @"count\s*\(", RegexOptions.IgnoreCase))
        {
            return await CountItemsAsync(sql, cancellationToken);
        }

        // Map-index write: insert into [<index>] ([Col]…) values (@Col…) — executed as scalar to
        // return the new index row Id. Cosmos has no auto-increment, so we allocate Id = MAX+1 and
        // store every parameter as a field on the index item.
        if (StartsWith(sql.TrimStart(), "insert"))
        {
            var table = ExtractTable(sql);
            var newId = await NextSequenceAsync(table, cancellationToken);

            var item = new JObject
            {
                ["id"] = $"{table}:{newId}",
                ["Id"] = newId,
            };

            foreach (DbParameter p in _parameters)
            {
                var name = p.ParameterName.TrimStart('@');
                item[name] = ToToken(p.Value is DBNull ? null : p.Value);
            }

            WithPartition(item, table);
            await CosmosContainer.UpsertItemAsync(item, PartitionKeyFor(table), cancellationToken: cancellationToken);
            Undo?.Record(item["id"]!.ToString(), PkValue(table), null);
            return newId;
        }

        throw new NotSupportedException($"Unsupported scalar statement: {CommandText}");
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var sql = CommandText;

        // A COUNT over a join run through the reader (raw Dapper QueryFirstOrDefaultAsync<int>, e.g. the
        // Inner/Left/Right join count API) — compute the matching-DocumentId count and yield it as a single
        // "count" column, before the join branches treat it as a row-returning query.
        if (Regex.IsMatch(sql, @"\bcount\s*\(", RegexOptions.IgnoreCase) && Regex.IsMatch(sql, @"\bjoin\b", RegexOptions.IgnoreCase))
        {
            var joinCount = await CountJoinAsync(sql, cancellationToken);
            return new CosmosDbDataReader(["count"], [[(object?)joinCount]]);
        }

        // Reduce-index query — a doc↔bridge↔index three-way join, recognised by the index↔bridge join
        // "ON a.[Id] = b.[<X>Id]". Resolve via index → bridge → documents.
        if (Regex.IsMatch(sql, @"\bon\s+\w+\.\[Id\]\s*=\s*\w+\.\[\w+Id\]", RegexOptions.IgnoreCase))
        {
            return await ExecuteReduceJoinQueryAsync(sql, cancellationToken);
        }

        // Multi-index join across DISTINCT index tables (.With<I1>().With<I2>()) — intersect each
        // index's DocumentId set (INNER JOIN = AND). The same index joined repeatedly (scope / boolean
        // queries) stays on the single-index path, where its combined WHERE translates correctly.
        if (IndexJoinTables(sql).Distinct().Count() >= 2)
        {
            return await ExecuteMultiIndexJoinQueryAsync(sql, cancellationToken);
        }

        // An index join ("JOIN [index] AS a ON a.[DocumentId] = …") — whether flat (FirstOrDefault) or
        // wrapped in a "(SELECT … GROUP BY …)" dedup subquery (ListAsync) — is an index query.
        if (Regex.IsMatch(sql, @"join\s+\[[^\]]+\]\s+as\s+\w+\s+on\s+\w+\.\[DocumentId\]", RegexOptions.IgnoreCase))
        {
            return await ExecuteIndexJoinQueryAsync(sql, cancellationToken);
        }

        // A join onto a "(SELECT … )" subquery with no index inside is the document-by-type form of
        // Query<T>().ListAsync().
        if (Regex.IsMatch(sql, @"\bjoin\b", RegexOptions.IgnoreCase))
        {
            return await QueryDocumentsAsync(sql, cancellationToken);
        }

        // A non-join COUNT executed through a reader (e.g. raw Dapper QueryFirstOrDefaultAsync<int>) rather
        // than ExecuteScalar — return the scalar count as a single "count" column so the reader yields it.
        if (Regex.IsMatch(sql, @"\bcount\s*\(", RegexOptions.IgnoreCase))
        {
            var count = await CountItemsAsync(sql, cancellationToken);
            return new CosmosDbDataReader(["count"], [[(object?)count]]);
        }

        if (StartsWith(sql.TrimStart(), "select"))
        {
            var table = ExtractTableAfter(sql, "from");

            // Scalar date-part projection: SELECT DateTimePart("<part>", [<col>]) FROM [<table>] — run it
            // as a Cosmos VALUE query over the partition so the computed int is returned, not a raw column.
            var dateFn = Regex.Match(sql, @"DateTimePart\(\s*""(\w+)""\s*,\s*\[(\w+)\]\s*\)", RegexOptions.IgnoreCase);
            if (dateFn.Success)
            {
                return await ExecuteDatePartAsync(sql, table, dateFn.Groups[1].Value, dateFn.Groups[2].Value, cancellationToken);
            }

            if (IsDocumentTable(table))
            {
                // Load by id(s): WHERE [Id] = @Id / IN (…) — the only params are ids; point-read each.
                if (Regex.IsMatch(sql, @"\[id\]\s*(=|in\b)", RegexOptions.IgnoreCase))
                {
                    var rows = new System.Collections.Generic.List<object?[]>();
                    foreach (DbParameter p in _parameters)
                    {
                        if (p.Value is null or DBNull)
                        {
                            continue;
                        }

                        var id = Convert.ToInt64(p.Value);
                        try
                        {
                            var resp = await CosmosContainer.ReadItemAsync<JObject>($"{table}:{id}", PartitionKeyFor(table), cancellationToken: cancellationToken);
                            rows.Add(ToRow(resp.Resource));
                        }
                        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                        {
                            // no row for this id
                        }
                    }

                    return new CosmosDbDataReader(DocumentColumns, rows);
                }

                // Otherwise a document query: all documents in the partition, optionally filtered by Type.
                return await QueryDocumentsAsync(sql, cancellationToken);
            }

            // Index-row query: SELECT * FROM [index] AS a [WHERE …] [LIMIT n] → return index items.
            return await QueryIndexRowsAsync(sql, table, cancellationToken);
        }

        throw new NotSupportedException($"Unsupported query statement: {CommandText}");
    }

    // ---- sync path delegates to async ----

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    // ---- helpers ----

    // Parse an index-joined query and run the index lookup, returning distinct DocumentIds (ordered if
    // the query has an ORDER BY). Shared by the reader (then point-reads) and CountAsync.
    private async Task<System.Collections.Generic.List<long>> GatherDocumentIdsAsync(string sql, CancellationToken cancellationToken)
    {
        // Accept both the .With() form ("… = [Document].[Id]") and the raw SqlBuilder join form
        // ("… = d.[Id]", aliased) so InnerJoin/LeftJoin/RightJoin over Document⋈Index parse.
        var join = Regex.Match(sql,
            @"join\s+\[([^\]]+)\]\s+as\s+(\w+)\s+on\s+\w+\.\[DocumentId\]\s*=\s*(?:\w+|\[[^\]]+\])\.\[Id\]",
            RegexOptions.IgnoreCase);

        if (!join.Success)
        {
            throw new NotSupportedException($"Unsupported join query: {CommandText}");
        }

        var indexTable = join.Groups[1].Value;
        var alias = join.Groups[2].Value;

        // WHERE predicate over index columns → Cosmos predicate. Strip the document-Type predicate
        // YesSql adds (it does not apply inside the index partition), then rewrite column refs.
        _ = alias; // columns are rewritten generically by TranslateWhere
        var cosmosWhere = string.Empty;
        var where = ExtractWhere(sql);
        if (!string.IsNullOrWhiteSpace(where))
        {
            var stripped = StripDocTypePredicate(where!).Trim();
            stripped = await ResolveSubqueriesAsync(stripped, cancellationToken);
            if (stripped.Length > 0)
            {
                cosmosWhere = TranslateWhere(stripped);
            }
        }

        // Ordering: Cosmos ORDER BY is case-sensitive and can't ORDER BY LOWER(...), so when the query is
        // ordered we fetch DocumentId + the order columns and sort client-side (case-insensitive, matching
        // the reference dialects). Unordered queries keep the cheap "SELECT VALUE c.DocumentId".
        var orderTerms = ParseOrderTerms(sql);
        // DocumentId is already projected, so don't re-select it (Cosmos rejects the duplicate property).
        var extraOrderCols = orderTerms.Select(t => t.Column).Distinct()
            .Where(col => !col.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)).ToList();
        var projection = orderTerms.Count == 0
            ? "VALUE c.DocumentId"
            : "c.DocumentId" + string.Concat(extraOrderCols.Select(col => $", c[\"{col}\"]"));

        var queryText = "SELECT " + projection + " FROM c WHERE " + Scoped(indexTable)
            + (cosmosWhere.Length > 0 ? " AND " + cosmosWhere : string.Empty);
        var queryDef = new QueryDefinition(queryText).WithParameter("@pk", PkValue(indexTable));
        foreach (DbParameter p in _parameters)
        {
            queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

        var documentIds = new System.Collections.Generic.List<long>();
        try
        {
            if (orderTerms.Count == 0)
            {
                using var iterator = CosmosContainer.GetItemQueryIterator<long>(queryDef,
                    requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(indexTable) });
                while (iterator.HasMoreResults)
                {
                    foreach (var docId in await iterator.ReadNextAsync(cancellationToken))
                    {
                        if (!documentIds.Contains(docId))
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
                    if (!documentIds.Contains(docId))
                    {
                        documentIds.Add(docId);
                    }
                }
            }
        }
        catch (CosmosException ex)
        {
            throw new NotSupportedException($"IDXQ_FAIL cosmos=[{queryText}] orig=[{CommandText}]: {ex.Message}", ex);
        }

        // filterType:true adds a "[Document].[Type] = @p" predicate that StripDocTypePredicate removed (it
        // can't run inside the index partition). Re-apply it: keep only gathered ids whose document has that
        // exact Type. Without this, a Query<SubClass>(filterType:true) counts every subclass, not just one.
        var typeMatch = where is null ? Match.Empty : Regex.Match(where, @"\[[^\]]+\]\.\[Type\]\s*=\s*@(\w+)", RegexOptions.IgnoreCase);
        if (typeMatch.Success && documentIds.Count > 0)
        {
            var typeParam = typeMatch.Groups[1].Value;
            object? typeValue = null;
            foreach (DbParameter p in _parameters)
            {
                if (p.ParameterName.TrimStart('@').Equals(typeParam, StringComparison.OrdinalIgnoreCase))
                {
                    typeValue = p.Value is DBNull ? null : p.Value;
                    break;
                }
            }

            var docTable = ExtractTableAfter(sql, "from");
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

    // Translate the SQL ORDER BY (which aggregates index columns as "MAX(a.[Col]) AS order_N" under the
    // GROUP BY) into a Cosmos "ORDER BY c["Col"] [DESC]" clause.
    // Parse the trailing ORDER BY into (column, descending) pairs for client-side sorting.
    private static System.Collections.Generic.List<(string Column, bool Desc)> ParseOrderTerms(string sql)
    {
        var result = new System.Collections.Generic.List<(string, bool)>();
        var orderBys = Regex.Matches(sql, @"order\s+by\s+(.+?)(?:\boffset\b|\)|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (orderBys.Count == 0)
        {
            return result;
        }

        var aliasToColumn = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"\(\s*\w+\.\[([^\]]+)\]\s*\)\s+as\s+(order_\d+)", RegexOptions.IgnoreCase))
        {
            aliasToColumn[m.Groups[2].Value] = m.Groups[1].Value;
        }

        foreach (var raw in orderBys[^1].Groups[1].Value.Split(','))
        {
            var term = raw.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            var desc = Regex.IsMatch(term, @"\bdesc\b", RegexOptions.IgnoreCase);
            var expr = Regex.Replace(term, @"\s+(asc|desc)\b", string.Empty, RegexOptions.IgnoreCase).Trim();

            string? column = null;
            if (aliasToColumn.TryGetValue(expr, out var mapped))
            {
                column = mapped;
            }
            else
            {
                var col = Regex.Match(expr, @"\[([^\]]+)\]");
                if (col.Success)
                {
                    column = col.Groups[1].Value;
                }
            }

            if (column != null)
            {
                result.Add((column, desc));
            }
        }

        return result;
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
    private static System.Collections.Generic.IEnumerable<JObject> OrderRows(
        System.Collections.Generic.List<JObject> rows, System.Collections.Generic.List<(string Column, bool Desc)> orderTerms)
        => rows
            .Select((row, index) => (Row: row, Index: index))
            .OrderBy(x => x, System.Collections.Generic.Comparer<(JObject Row, int Index)>.Create((x, y) =>
            {
                foreach (var (column, desc) in orderTerms)
                {
                    var c = CompareTokens(x.Row[column], y.Row[column]);
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

    private static string BuildOrderClause(string sql)
    {
        var orderBys = Regex.Matches(sql, @"order\s+by\s+(.+?)(?:\boffset\b|\)|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (orderBys.Count == 0)
        {
            return string.Empty;
        }

        // GROUP BY form aggregates the order column as "MAX(a.[Col]) AS order_N"; map alias → column.
        var aliasToColumn = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"\(\s*\w+\.\[([^\]]+)\]\s*\)\s+as\s+(order_\d+)", RegexOptions.IgnoreCase))
        {
            aliasToColumn[m.Groups[2].Value] = m.Groups[1].Value;
        }

        var terms = new System.Collections.Generic.List<string>();
        foreach (var raw in orderBys[orderBys.Count - 1].Groups[1].Value.Split(','))
        {
            var term = raw.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            var desc = Regex.IsMatch(term, @"\bdesc\b", RegexOptions.IgnoreCase);
            var expr = Regex.Replace(term, @"\s+(asc|desc)\b", string.Empty, RegexOptions.IgnoreCase).Trim();

            string? column = null;
            if (aliasToColumn.TryGetValue(expr, out var mapped))
            {
                column = mapped;       // aggregate alias (order_N)
            }
            else
            {
                var col = Regex.Match(expr, @"\[([^\]]+)\]");   // direct column ref: alias.[Col] or [Col]
                if (col.Success)
                {
                    column = col.Groups[1].Value;
                }
            }

            if (column != null)
            {
                terms.Add($"c[\"{column}\"]" + (desc ? " DESC" : string.Empty));
            }
        }

        return terms.Count > 0 ? " ORDER BY " + string.Join(", ", terms) : string.Empty;
    }

    private async Task<DbDataReader> ExecuteIndexJoinQueryAsync(string sql, CancellationToken cancellationToken)
    {
        var documentTable = Regex.Match(sql,
            @"join\s+\[[^\]]+\]\s+as\s+\w+\s+on\s+\w+\.\[DocumentId\]\s*=\s*\[([^\]]+)\]\.\[Id\]",
            RegexOptions.IgnoreCase).Groups[1].Value;

        var documentIds = await GatherDocumentIdsAsync(sql, cancellationToken);

        IEnumerable<long> page = documentIds.Skip(ExtractOffset(sql));
        var limit = ExtractLimit(sql);
        if (limit.HasValue)
        {
            page = page.Take(limit.Value);
        }

        var rows = new List<object?[]>();
        foreach (var docId in page)
        {
            try
            {
                var resp = await CosmosContainer.ReadItemAsync<JObject>($"{documentTable}:{docId}", PartitionKeyFor(documentTable), cancellationToken: cancellationToken);
                rows.Add(ToRow(resp.Resource));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // document missing
            }
        }

        return new CosmosDbDataReader(DocumentColumns, rows);
    }

    // Resolve "[Col] [NOT] IN (SELECT [c] FROM [t] AS a [WHERE …])" by executing the inner query and
    // substituting a literal IN list (Cosmos has no cross-partition correlated subqueries).
    private async Task<string> ResolveSubqueriesAsync(string where, CancellationToken cancellationToken)
    {
        while (true)
        {
            var m = Regex.Match(where, @"((?:(?:\w+|\[[^\]]+\])\.)?\[[^\]]+\])\s+(not\s+)?in\s*\(\s*select\b",
                RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return where;
            }

            // Balanced scan for the subquery's closing paren.
            var openIdx = where.IndexOf('(', m.Index);
            int depth = 0, closeIdx = -1;
            for (var i = openIdx; i < where.Length; i++)
            {
                if (where[i] == '(')
                {
                    depth++;
                }
                else if (where[i] == ')' && --depth == 0)
                {
                    closeIdx = i;
                    break;
                }
            }

            if (closeIdx < 0)
            {
                return where; // malformed; leave as-is
            }

            var subquery = where.Substring(openIdx + 1, closeIdx - openIdx - 1);
            var sm = Regex.Match(subquery,
                @"select\s+(?:(?:\w+|\[[^\]]+\])\.)?\[([^\]]+)\]\s+from\s+\[([^\]]+)\]\s+as\s+\w+(?:\s+where\s+(.*))?$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!sm.Success)
            {
                return where;
            }

            var innerColumn = sm.Groups[1].Value;
            var innerTable = sm.Groups[2].Value;
            var innerWhere = sm.Groups[3].Success ? sm.Groups[3].Value.Trim() : null;

            var innerCosmosWhere = string.IsNullOrWhiteSpace(innerWhere) ? string.Empty : " AND " + TranslateWhere(innerWhere!);
            var queryDef = new QueryDefinition($"SELECT VALUE c[\"{innerColumn}\"] FROM c WHERE " + Scoped(innerTable, "@__itbl") + innerCosmosWhere)
                .WithParameter("@__itbl", PkValue(innerTable));
            foreach (DbParameter p in _parameters)
            {
                queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
            }

            var literals = new List<string>();
            using (var iterator = CosmosContainer.GetItemQueryIterator<JToken>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(innerTable) }))
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var v in await iterator.ReadNextAsync(cancellationToken))
                    {
                        literals.Add(ToLiteral(v));
                    }
                }
            }

            var list = literals.Count > 0 ? string.Join(", ", literals) : "null";
            var replacement = $"{m.Groups[1].Value} {(m.Groups[2].Success ? "NOT " : string.Empty)}IN ({list})";
            where = where[..m.Index] + replacement + where[(closeIdx + 1)..];
        }
    }

    private static string ToLiteral(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return "null";
        }

        return token.Type == JTokenType.String
            ? "'" + token.ToString().Replace("'", "\\'") + "'"
            : token.ToString();
    }

    private static bool IsDocumentTable(string table) => table.EndsWith("Document", StringComparison.OrdinalIgnoreCase);

    // Count the matching DocumentIds for a COUNT over a join (reduce / multi-index / single-index). Shared
    // by the scalar path (CountAsync) and the reader path (raw Inner/Left/Right join count API).
    private async Task<long> CountJoinAsync(string sql, CancellationToken cancellationToken)
    {
        System.Collections.Generic.List<long> ids;
        if (Regex.IsMatch(sql, @"\bon\s+\w+\.\[Id\]\s*=\s*\w+\.\[\w+Id\]", RegexOptions.IgnoreCase))
        {
            ids = await GatherReduceDocumentIdsAsync(sql, cancellationToken);
        }
        else if (IndexJoinTables(sql).Distinct().Count() >= 2)
        {
            ids = await GatherMultiIndexDocumentIdsAsync(sql, cancellationToken);
        }
        else
        {
            ids = await GatherDocumentIdsAsync(sql, cancellationToken);
        }

        return ids.Count;
    }

    // Count items in a partition: SELECT count(...) FROM [<table>] [WHERE <predicate>]. Shared by the
    // scalar path (CountAsync) and the reader path (raw Dapper QueryFirstOrDefaultAsync<int>).
    private async Task<long> CountItemsAsync(string sql, CancellationToken cancellationToken)
    {
        var table = ExtractTableAfter(sql, "from");
        var where = ExtractWhere(sql);
        var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

        var queryDef = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE " + Scoped(table) + cosmosWhere)
            .WithParameter("@pk", PkValue(table));
        foreach (DbParameter p in _parameters)
        {
            queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

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

    // Split a comma-separated list at top level, respecting single-quoted strings and nested parentheses.
    private static System.Collections.Generic.List<string> SplitTopLevelCommas(string s)
    {
        var parts = new System.Collections.Generic.List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\'')
            {
                inString = !inString;
            }
            else if (!inString && ch == '(')
            {
                depth++;
            }
            else if (!inString && ch == ')')
            {
                depth--;
            }
            else if (!inString && depth == 0 && ch == ',')
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }

        parts.Add(s[start..]);
        return parts;
    }

    // Parse a SQL literal (quoted string, number, bool, or NULL) into a JToken.
    private static JToken ParseSqlLiteral(string raw)
    {
        raw = raw.Trim();
        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return JValue.CreateNull();
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return new JValue(raw[1..^1].Replace("''", "'"));
        }

        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return new JValue(bool.Parse(raw));
        }

        if (long.TryParse(raw, out var l))
        {
            return new JValue(l);
        }

        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return new JValue(d);
        }

        return new JValue(raw);
    }

    private static string? ExtractWhere(string sql)
    {
        var m = Regex.Match(sql, @"\bwhere\b(.*?)(?:\bgroup\s+by\b|\border\s+by\b|\blimit\b|\boffset\b|\)\s*as\b|;|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim().TrimEnd(';').Trim() : null;
    }

    private static int? ExtractLimit(string sql)
    {
        var m = Regex.Match(sql, @"\blimit\s+(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
    }

    private static int ExtractOffset(string sql)
    {
        var m = Regex.Match(sql, @"\boffset\s+(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    // Push paging into the Cosmos query (OFFSET … LIMIT) so a BOUNDED result set is returned. Without this the
    // provider fetches every matching document in the partition and trims client-side — which is fast on the
    // Postgres-backed emulator but degrades to an effective hang on real Cosmos as a partition fills (it returns
    // the entire matching set just to take the first row). Cosmos requires OFFSET and LIMIT together; ORDER BY is
    // optional (already appended separately when present).
    private static string BuildOffsetLimitClause(string sql)
    {
        var limit = ExtractLimit(sql);
        return limit.HasValue ? $" OFFSET {ExtractOffset(sql)} LIMIT {limit.Value}" : string.Empty;
    }

    // Rewrite SQL column refs (alias.[Col], [table].[Col], or bare [Col]) → Cosmos c["Col"] (single
    // pass so an already-rewritten c["Col"] is not reprocessed), then map SQL null tests to Cosmos.
    private string TranslateWhere(string where)
    {
        where = Regex.Replace(where, @"(?:(?:\w+|\[[^\]]+\])\.)?\[([^\]]+)\]", "c[\"$1\"]");
        where = Regex.Replace(where, @"(c\[""[^""]+""\])\s+is\s+not\s+null", "(IS_DEFINED($1) AND NOT IS_NULL($1))", RegexOptions.IgnoreCase);
        where = Regex.Replace(where, @"(c\[""[^""]+""\])\s+is\s+null", "(NOT IS_DEFINED($1) OR IS_NULL($1))", RegexOptions.IgnoreCase);

        // Compare DateTime/DateTimeOffset parameters by instant (DateTimeToTimestamp) rather than by the raw
        // ISO text, so a DateTimeOffset field ("…+00:00") matches a DateTime value ("…Z") for the same moment.
        // Only predicates against a date parameter are wrapped, so non-date comparisons are untouched.
        foreach (DbParameter p in _parameters)
        {
            if (p.Value is not (DateTime or DateTimeOffset))
            {
                continue;
            }

            var paramRef = "@" + p.ParameterName.TrimStart('@');
            var escaped = Regex.Escape(paramRef);
            where = Regex.Replace(where, @"(c\[""[^""]+""\])\s*(=|!=|<>|<=|>=|<|>)\s*" + escaped + @"(?![\w])",
                "DateTimeToTimestamp($1) $2 DateTimeToTimestamp(" + paramRef + ")");
            where = Regex.Replace(where, escaped + @"(?![\w])\s*(=|!=|<>|<=|>=|<|>)\s*(c\[""[^""]+""\])",
                "DateTimeToTimestamp(" + paramRef + ") $1 DateTimeToTimestamp($2)");
        }

        return where;
    }

    // Remove the document-Type predicate ([Doc].[Type] = @p) YesSql adds to index joins.
    private static string StripDocTypePredicate(string where)
    {
        where = Regex.Replace(where, @"\[[^\]]+\]\.\[Type\]\s*=\s*@\w+\s+and\s+", "", RegexOptions.IgnoreCase);
        where = Regex.Replace(where, @"\s+and\s+\[[^\]]+\]\.\[Type\]\s*=\s*@\w+", "", RegexOptions.IgnoreCase);
        where = Regex.Replace(where, @"^\s*\[[^\]]+\]\.\[Type\]\s*=\s*@\w+\s*$", "", RegexOptions.IgnoreCase);
        return where;
    }

    // Query<T>() — all documents in the partition, optionally filtered by Type.
    private async Task<DbDataReader> QueryDocumentsAsync(string sql, CancellationToken cancellationToken)
    {
        var docTable = ExtractTableAfter(sql, "from");

        // Type filter: YesSql usually binds @Type, but some callers (e.g. Orchard's QueriesDocument
        // migration) embed a [Type] = '<literal>' directly in the WHERE. Honour both, otherwise the
        // filter is silently dropped and the query returns the wrong document(s).
        object? typeFilter = TryParam("Type", out var typeVal) ? typeVal : null;
        if (typeFilter is null)
        {
            var lit = Regex.Match(sql, @"\[Type\]\s*=\s*'([^']*)'", RegexOptions.IgnoreCase);
            if (lit.Success)
            {
                typeFilter = lit.Groups[1].Value;
            }
        }

        var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(docTable) + (typeFilter is not null ? " AND c.Type = @Type" : string.Empty) + BuildOrderClause(sql) + BuildOffsetLimitClause(sql))
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

        // OFFSET/LIMIT is now applied by Cosmos (BuildOffsetLimitClause); items is already the page.
        IEnumerable<JObject> page = items;

        // Honour the SELECT projection. Dapper reads result columns positionally, so a single-column
        // projection (e.g. "SELECT [Content]") must return exactly that column — returning the full
        // document row would make Dapper read [Id] (a number) where [Content] (a string) was asked for.
        var columns = ExtractDocumentSelectColumns(sql) ?? DocumentColumns;
        return new CosmosDbDataReader(columns, page.Select(item => ProjectRow(item, columns)).ToList());
    }

    // The document columns a SELECT projects, or null for "*" / "alias.*" (→ all DocumentColumns).
    private static string[]? ExtractDocumentSelectColumns(string sql)
    {
        var m = Regex.Match(sql, @"select\s+(.*?)\s+from\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success || m.Groups[1].Value.Contains('*'))
        {
            return null;
        }

        var cols = Regex.Matches(m.Groups[1].Value, @"\[(\w+)\]").Select(x => x.Groups[1].Value).ToArray();
        return cols.Length > 0 ? cols : null;
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
    private async Task<DbDataReader> ExecuteDatePartAsync(string sql, string table, string part, string column, CancellationToken cancellationToken)
    {
        var where = ExtractWhere(sql);
        var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

        var queryDef = new QueryDefinition($"SELECT VALUE DateTimePart(\"{part}\", c.{column}) FROM c WHERE " + Scoped(table) + cosmosWhere)
            .WithParameter("@pk", PkValue(table));
        foreach (DbParameter p in _parameters)
        {
            queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

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
    private async Task<DbDataReader> QueryIndexRowsAsync(string sql, string indexTable, CancellationToken cancellationToken)
    {
        var where = ExtractWhere(sql);
        var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

        var queryDef = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(indexTable) + cosmosWhere + BuildOrderClause(sql) + BuildOffsetLimitClause(sql))
            .WithParameter("@pk", PkValue(indexTable));
        foreach (DbParameter p in _parameters)
        {
            queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

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

        // OFFSET/LIMIT is now applied by Cosmos (BuildOffsetLimitClause); all is already the page.
        var items = all;
        var columns = new List<string>();
        foreach (var item in items)
        {
            foreach (var prop in item.Properties())
            {
                // Exclude the Cosmos envelope fields by exact (ordinal) name — the lowercase system "id",
                // "pk", and the "__table" discriminator — while keeping the index's own numeric "Id" column.
                if (!prop.Name.Equals("id", StringComparison.Ordinal)
                    && !prop.Name.Equals("pk", StringComparison.Ordinal)
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

    private static List<(string Table, string Alias)> IndexJoins(string sql)
        => Regex.Matches(sql, @"join\s+\[([^\]]+)\]\s+as\s+(\w+)\s+on\s+\w+\.\[DocumentId\]\s*=\s*\[[^\]]+\]\.\[Id\]", RegexOptions.IgnoreCase)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    private static IEnumerable<string> IndexJoinTables(string sql) => IndexJoins(sql).Select(j => j.Table);

    // Strip redundant outer parentheses that wrap the whole expression: "((A) AND (B))" → "(A) AND (B)".
    private static string UnwrapOuterParens(string s)
    {
        s = s.Trim();
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            var depth = 0;
            var wraps = true;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(')
                {
                    depth++;
                }
                else if (s[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i < s.Length - 1)
                    {
                        wraps = false;
                        break;
                    }
                }
            }

            if (!wraps)
            {
                break;
            }

            s = s[1..^1].Trim();
        }

        return s;
    }

    // Split a WHERE clause on top-level " AND " (respecting parentheses).
    private static List<string> SplitTopLevelAnd(string where)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < where.Length; i++)
        {
            if (where[i] == '(')
            {
                depth++;
            }
            else if (where[i] == ')')
            {
                depth--;
            }
            else if (depth == 0 && i + 5 <= where.Length && where.Substring(i, 5).Equals(" and ", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(where[start..i]);
                i += 4;
                start = i + 1;
            }
        }

        parts.Add(where[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    // Multi-index join across distinct index tables: query each index's DocumentId set (filtered by its
    // own aliases' predicates) and intersect them.
    private async Task<List<long>> GatherMultiIndexDocumentIdsAsync(string sql, CancellationToken cancellationToken)
    {
        var joins = IndexJoins(sql);
        var where = ExtractWhere(sql);
        var terms = string.IsNullOrWhiteSpace(where)
            ? new List<string>()
            : SplitTopLevelAnd(UnwrapOuterParens(StripDocTypePredicate(where!).Trim()));

        List<long>? result = null;
        foreach (var group in joins.GroupBy(j => j.Table))
        {
            var aliases = group.Select(j => j.Alias).ToList();
            var tableTerms = terms.Where(t => aliases.Any(a => Regex.IsMatch(t, @"\b" + Regex.Escape(a) + @"\.", RegexOptions.IgnoreCase))).ToList();
            var sub = tableTerms.Count > 0 ? " AND " + TranslateWhere(string.Join(" AND ", tableTerms)) : string.Empty;

            var queryDef = new QueryDefinition("SELECT VALUE c.DocumentId FROM c WHERE " + Scoped(group.Key) + sub).WithParameter("@pk", PkValue(group.Key));
            foreach (DbParameter p in _parameters)
            {
                queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
            }

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
        var orderTerms = ParseOrderTerms(sql);
        if (orderTerms.Count > 0 && documentIds.Count > 0)
        {
            var orderCols = orderTerms.Select(t => t.Column).Distinct()
                .Where(col => !col.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)).ToList();
            var orderValues = new Dictionary<long, JObject>();
            foreach (var group in joins.GroupBy(j => j.Table))
            {
                var projection = "c.DocumentId" + string.Concat(orderCols.Select(col => $", c[\"{col}\"]"));
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
                        var c = column.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)
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

    private async Task<DbDataReader> ExecuteMultiIndexJoinQueryAsync(string sql, CancellationToken cancellationToken)
    {
        var documentTable = ExtractTableAfter(sql, "from");
        var documentIds = await GatherMultiIndexDocumentIdsAsync(sql, cancellationToken);

        IEnumerable<long> page = documentIds.Skip(ExtractOffset(sql));
        var limit = ExtractLimit(sql);
        if (limit.HasValue)
        {
            page = page.Take(limit.Value);
        }

        var rows = new List<object?[]>();
        foreach (var docId in page)
        {
            try
            {
                var resp = await CosmosContainer.ReadItemAsync<JObject>($"{documentTable}:{docId}", PartitionKeyFor(documentTable), cancellationToken: cancellationToken);
                rows.Add(ToRow(resp.Resource));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // document missing
            }
        }

        return new CosmosDbDataReader(DocumentColumns, rows);
    }

    // Reduce-index query: doc ← bridge → index. Resolve in three steps — matching index Ids, then the
    // bridge rows linking them to documents, then the document ids.
    private async Task<List<long>> GatherReduceDocumentIdsAsync(string sql, CancellationToken cancellationToken)
    {
        // index↔bridge join: "JOIN [Index] AS idx ON idx.[Id] = <bridgeAlias>.[<FK>]". Capture the bridge
        // alias so we pick the RIGHT bridge — a query may also join plain map indexes (.With<Map>()) whose
        // "[DocumentId] = [Document].[Id]" join looks identical to the reduce bridge's.
        var index = Regex.Match(sql, @"join\s+\[([^\]]+)\]\s+as\s+(\w+)\s+on\s+\w+\.\[Id\]\s*=\s*(\w+)\.\[(\w+)\]", RegexOptions.IgnoreCase);
        if (!index.Success)
        {
            throw new NotSupportedException($"Unsupported reduce query: {CommandText}");
        }

        var indexTable = index.Groups[1].Value;
        var bridgeAlias = index.Groups[3].Value;
        var bridgeForeignKey = index.Groups[4].Value;

        var bridge = Regex.Match(sql, @"join\s+\[([^\]]+)\]\s+as\s+" + Regex.Escape(bridgeAlias) + @"\s+on\s+" + Regex.Escape(bridgeAlias) + @"\.\[DocumentId\]\s*=\s*(?:\w+|\[[^\]]+\])\.\[Id\]", RegexOptions.IgnoreCase);
        if (!bridge.Success)
        {
            throw new NotSupportedException($"Unsupported reduce query: {CommandText}");
        }

        var bridgeTable = bridge.Groups[1].Value;

        var where = ExtractWhere(sql);
        var indexWhere = string.Empty;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var stripped = StripDocTypePredicate(where!).Trim();
            if (stripped.Length > 0)
            {
                indexWhere = " AND " + TranslateWhere(stripped);
            }
        }

        // 1. matching index rows
        var indexQuery = new QueryDefinition("SELECT VALUE c.Id FROM c WHERE " + Scoped(indexTable) + indexWhere).WithParameter("@pk", PkValue(indexTable));
        foreach (DbParameter p in _parameters)
        {
            indexQuery = indexQuery.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

        var indexIds = new List<long>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<long>(indexQuery,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(indexTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var v in await iterator.ReadNextAsync(cancellationToken))
                {
                    indexIds.Add(v);
                }
            }
        }

        if (indexIds.Count == 0)
        {
            return new List<long>();
        }

        // 2. bridge rows linking those index rows to documents
        var bridgeQuery = new QueryDefinition(
            $"SELECT VALUE c.DocumentId FROM c WHERE " + Scoped(bridgeTable) + $" AND c[\"{bridgeForeignKey}\"] IN ({string.Join(", ", indexIds)})")
            .WithParameter("@pk", PkValue(bridgeTable));

        var documentIds = new List<long>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<long>(bridgeQuery,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(bridgeTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var v in await iterator.ReadNextAsync(cancellationToken))
                {
                    if (!documentIds.Contains(v))
                    {
                        documentIds.Add(v);
                    }
                }
            }
        }

        // A reduce query may also join plain map indexes (.With<Map>().With<Reduce>()). Intersect: keep only
        // documents that also have a row in each such map index (the bridge itself is excluded by alias).
        foreach (var (mapTable, mapAlias) in IndexJoins(sql))
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

    private async Task<DbDataReader> ExecuteReduceJoinQueryAsync(string sql, CancellationToken cancellationToken)
    {
        var documentTable = Regex.Match(sql, @"from\s+\[([^\]]+)\]", RegexOptions.IgnoreCase).Groups[1].Value;
        var documentIds = await GatherReduceDocumentIdsAsync(sql, cancellationToken);

        IEnumerable<long> page = documentIds.Skip(ExtractOffset(sql));
        var limit = ExtractLimit(sql);
        if (limit.HasValue)
        {
            page = page.Take(limit.Value);
        }

        var rows = new List<object?[]>();
        foreach (var docId in page)
        {
            try
            {
                var resp = await CosmosContainer.ReadItemAsync<JObject>($"{documentTable}:{docId}", PartitionKeyFor(documentTable), cancellationToken: cancellationToken);
                rows.Add(ToRow(resp.Resource));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // document missing
            }
        }

        return new CosmosDbDataReader(DocumentColumns, rows);
    }

    // Monotonic, never-reused id allocator for index rows (auto-increment has no Cosmos equivalent, and
    // MAX+1 reuses ids after deletes — which breaks YesSql's append-only index expectations). A counter
    // doc per table lives in an isolated "__seq" partition so it never appears in index/count queries.
    private async Task<long> NextSequenceAsync(string table, CancellationToken cancellationToken)
    {
        var seqPk = new PartitionKey("__seq");

        for (var attempt = 0; attempt < 8; attempt++)
        {
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
                    await CosmosContainer.CreateItemAsync(new JObject { ["id"] = table, ["pk"] = "__seq", ["next"] = seed }, seqPk, cancellationToken: cancellationToken);
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
        _ => JToken.FromObject(value),
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

    private static bool StartsWith(string sql, string keyword)
        => sql.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);

    // First bracketed token in the statement (table appears before columns for insert/update/delete).
    private static string ExtractTable(string sql)
    {
        var m = Regex.Match(sql, @"\[([^\]]+)\]");
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException($"No table in: {sql}");
    }

    // First bracketed token following a keyword (e.g. the table after 'from').
    private static string ExtractTableAfter(string sql, string keyword)
    {
        var m = Regex.Match(sql, keyword + @"\s+\[([^\]]+)\]", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : ExtractTable(sql);
    }
}
