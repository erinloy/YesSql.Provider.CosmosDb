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

    // ---- async (primary) path, used by Dapper via QueryAsync/ExecuteAsync ----

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var sql = CommandText.TrimStart();

        // Reduce-index bridge row (Index↔Document link). The columns (e.g. [ArticlesByDayId],
        // [DocumentId]) don't match the param names (@Id, @DocumentId), so map columns→params by
        // position. Composite key (<indexFk>:<documentId>) — many rows share an index Id.
        if (StartsWith(sql, "insert") && TryParam("DocumentId", out var bridgeDocId) && !TryParam("Type", out _) && !TryParam("Content", out _))
        {
            var bridgeTable = ExtractTable(sql);
            var cv = Regex.Match(sql, @"\(([^)]*)\)\s*values\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
            var columns = cv.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('[', ']')).ToArray();
            var values = cv.Groups[2].Value.Split(',').Select(v => v.Trim().TrimStart('@')).ToArray();

            var bridge = new JObject { ["pk"] = bridgeTable };
            for (var i = 0; i < columns.Length && i < values.Length; i++)
            {
                bridge[columns[i]] = ToToken(TryParam(values[i], out var pv) ? pv : null);
            }

            var indexFk = columns.Length > 0 ? bridge[columns[0]]?.ToString() : "0";
            bridge["id"] = $"{bridgeTable}:{indexFk}:{bridgeDocId}";

            await CosmosContainer.UpsertItemAsync(bridge, new PartitionKey(bridgeTable), cancellationToken: cancellationToken);
            return 1;
        }

        if (StartsWith(sql, "insert") || StartsWith(sql, "update"))
        {
            var table = ExtractTable(sql);
            var id = Convert.ToInt64(Param("Id"));

            // UPDATE only carries the columns in its SET clause (Content/Version), so read the existing
            // item and patch the provided fields; INSERT carries all of them.
            JObject? item = null;
            if (StartsWith(sql, "update"))
            {
                try
                {
                    item = (await CosmosContainer.ReadItemAsync<JObject>($"{table}:{id}", new PartitionKey(table), cancellationToken: cancellationToken)).Resource;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // fall through to a fresh item
                }
            }

            item ??= new JObject { ["id"] = $"{table}:{id}", ["pk"] = table, ["Id"] = id };

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

            await CosmosContainer.UpsertItemAsync(item, new PartitionKey(table), cancellationToken: cancellationToken);
            return 1;
        }

        if (StartsWith(sql, "delete"))
        {
            // General delete: query items in the partition matching the WHERE (by [Id] for documents,
            // by [DocumentId] for map indexes, by composite key for reduce bridge rows), then delete each.
            var table = ExtractTable(sql);
            var where = ExtractWhere(sql);
            var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

            var queryDef = new QueryDefinition("SELECT c.id FROM c WHERE c.pk = @pk" + cosmosWhere).WithParameter("@pk", table);
            foreach (DbParameter p in _parameters)
            {
                queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
            }

            var ids = new List<string>();
            using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(table) }))
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                    {
                        ids.Add(item["id"]!.ToString());
                    }
                }
            }

            foreach (var id in ids)
            {
                try
                {
                    await CosmosContainer.DeleteItemAsync<JObject>(id, new PartitionKey(table), cancellationToken: cancellationToken);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // already gone
                }
            }

            return ids.Count;
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
            var ids = Regex.IsMatch(sql, @"\bon\s+\w+\.\[Id\]\s*=\s*\w+\.\[\w+Id\]", RegexOptions.IgnoreCase)
                ? await GatherReduceDocumentIdsAsync(sql, cancellationToken)
                : await GatherDocumentIdsAsync(sql, cancellationToken);
            return ids.Count;
        }

        // CountAsync without a join: SELECT count(*) FROM [<table>] [WHERE <predicate>] — count items in
        // that partition (documents by Type, or index rows).
        if (Regex.IsMatch(sql, @"count\s*\(", RegexOptions.IgnoreCase))
        {
            var table = ExtractTableAfter(sql, "from");
            var where = ExtractWhere(sql);
            var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

            var queryDef = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.pk = @pk" + cosmosWhere)
                .WithParameter("@pk", table);
            foreach (DbParameter p in _parameters)
            {
                queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
            }

            using var iterator = CosmosContainer.GetItemQueryIterator<long>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(table) });
            while (iterator.HasMoreResults)
            {
                foreach (var n in await iterator.ReadNextAsync(cancellationToken))
                {
                    return n;
                }
            }

            return 0L;
        }

        // Map-index write: insert into [<index>] ([Col]…) values (@Col…) — executed as scalar to
        // return the new index row Id. Cosmos has no auto-increment, so we allocate Id = MAX+1 and
        // store every parameter as a field on the index item.
        if (StartsWith(sql.TrimStart(), "insert"))
        {
            var table = ExtractTable(sql);
            var newId = (await MaxIdAsync(table, cancellationToken) ?? 0) + 1;

            var item = new JObject
            {
                ["id"] = $"{table}:{newId}",
                ["pk"] = table,
                ["Id"] = newId,
            };

            foreach (DbParameter p in _parameters)
            {
                var name = p.ParameterName.TrimStart('@');
                item[name] = ToToken(p.Value is DBNull ? null : p.Value);
            }

            await CosmosContainer.UpsertItemAsync(item, new PartitionKey(table), cancellationToken: cancellationToken);
            return newId;
        }

        throw new NotSupportedException($"Unsupported scalar statement: {CommandText}");
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var sql = CommandText;

        // Reduce-index query — a doc↔bridge↔index three-way join, recognised by the index↔bridge join
        // "ON a.[Id] = b.[<X>Id]". Resolve via index → bridge → documents.
        if (Regex.IsMatch(sql, @"\bon\s+\w+\.\[Id\]\s*=\s*\w+\.\[\w+Id\]", RegexOptions.IgnoreCase))
        {
            return await ExecuteReduceJoinQueryAsync(sql, cancellationToken);
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

        if (StartsWith(sql.TrimStart(), "select"))
        {
            var table = ExtractTableAfter(sql, "from");

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
                            var resp = await CosmosContainer.ReadItemAsync<JObject>($"{table}:{id}", new PartitionKey(table), cancellationToken: cancellationToken);
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
        var join = Regex.Match(sql,
            @"join\s+\[([^\]]+)\]\s+as\s+(\w+)\s+on\s+\w+\.\[DocumentId\]\s*=\s*\[[^\]]+\]\.\[Id\]",
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

        var queryText = "SELECT VALUE c.DocumentId FROM c WHERE c.pk = @pk"
            + (cosmosWhere.Length > 0 ? " AND " + cosmosWhere : string.Empty)
            + BuildOrderClause(sql);
        var queryDef = new QueryDefinition(queryText).WithParameter("@pk", indexTable);
        foreach (DbParameter p in _parameters)
        {
            queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

        var documentIds = new System.Collections.Generic.List<long>();
        try
        {
            using var iterator = CosmosContainer.GetItemQueryIterator<long>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(indexTable) });
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
        catch (CosmosException ex)
        {
            throw new NotSupportedException($"IDXQ_FAIL cosmos=[{queryText}] orig=[{CommandText}]: {ex.Message}", ex);
        }

        return documentIds;
    }

    // Translate the SQL ORDER BY (which aggregates index columns as "MAX(a.[Col]) AS order_N" under the
    // GROUP BY) into a Cosmos "ORDER BY c["Col"] [DESC]" clause.
    private static string BuildOrderClause(string sql)
    {
        var aliasToColumn = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"\(\s*\w+\.\[([^\]]+)\]\s*\)\s+as\s+(order_\d+)", RegexOptions.IgnoreCase))
        {
            aliasToColumn[m.Groups[2].Value] = m.Groups[1].Value;
        }

        if (aliasToColumn.Count == 0)
        {
            return string.Empty;
        }

        var orderBys = Regex.Matches(sql, @"order\s+by\s+(.+?)(?:\boffset\b|\)|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (orderBys.Count == 0)
        {
            return string.Empty;
        }

        var terms = new System.Collections.Generic.List<string>();
        foreach (var raw in orderBys[orderBys.Count - 1].Groups[1].Value.Split(','))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && aliasToColumn.TryGetValue(parts[0], out var col))
            {
                var desc = parts.Length > 1 && parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);
                terms.Add($"c[\"{col}\"]" + (desc ? " DESC" : string.Empty));
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
                var resp = await CosmosContainer.ReadItemAsync<JObject>($"{documentTable}:{docId}", new PartitionKey(documentTable), cancellationToken: cancellationToken);
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
            var queryDef = new QueryDefinition($"SELECT VALUE c[\"{innerColumn}\"] FROM c WHERE c.pk = @__itbl" + innerCosmosWhere)
                .WithParameter("@__itbl", innerTable);
            foreach (DbParameter p in _parameters)
            {
                queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
            }

            var literals = new List<string>();
            using (var iterator = CosmosContainer.GetItemQueryIterator<JToken>(queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(innerTable) }))
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

    // Rewrite SQL column refs (alias.[Col], [table].[Col], or bare [Col]) → Cosmos c["Col"] (single
    // pass so an already-rewritten c["Col"] is not reprocessed), then map SQL null tests to Cosmos.
    private static string TranslateWhere(string where)
    {
        where = Regex.Replace(where, @"(?:(?:\w+|\[[^\]]+\])\.)?\[([^\]]+)\]", "c[\"$1\"]");
        where = Regex.Replace(where, @"(c\[""[^""]+""\])\s+is\s+not\s+null", "(IS_DEFINED($1) AND NOT IS_NULL($1))", RegexOptions.IgnoreCase);
        where = Regex.Replace(where, @"(c\[""[^""]+""\])\s+is\s+null", "(NOT IS_DEFINED($1) OR IS_NULL($1))", RegexOptions.IgnoreCase);
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
        var hasType = TryParam("Type", out var typeVal);

        var queryDef = new QueryDefinition("SELECT * FROM c WHERE c.pk = @pk" + (hasType ? " AND c.Type = @Type" : string.Empty) + BuildOrderClause(sql))
            .WithParameter("@pk", docTable);
        if (hasType)
        {
            queryDef = queryDef.WithParameter("@Type", typeVal);
        }

        var items = new List<JObject>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(docTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                {
                    items.Add(item);
                }
            }
        }

        IEnumerable<JObject> page = items.Skip(ExtractOffset(sql));
        var limit = ExtractLimit(sql);
        if (limit.HasValue)
        {
            page = page.Take(limit.Value);
        }

        return new CosmosDbDataReader(DocumentColumns, page.Select(ToRow).ToList());
    }

    // Query<TIndex>() — return the index rows themselves (dynamic columns from the index fields).
    private async Task<DbDataReader> QueryIndexRowsAsync(string sql, string indexTable, CancellationToken cancellationToken)
    {
        var where = ExtractWhere(sql);
        var cosmosWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(where!);

        var queryDef = new QueryDefinition("SELECT * FROM c WHERE c.pk = @pk" + cosmosWhere + BuildOrderClause(sql))
            .WithParameter("@pk", indexTable);
        foreach (DbParameter p in _parameters)
        {
            queryDef = queryDef.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

        var all = new List<JObject>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JObject>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(indexTable) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var item in await iterator.ReadNextAsync(cancellationToken))
                {
                    all.Add(item);
                }
            }
        }

        IEnumerable<JObject> paged = all.Skip(ExtractOffset(sql));
        var limit = ExtractLimit(sql);
        if (limit.HasValue)
        {
            paged = paged.Take(limit.Value);
        }

        var items = paged.ToList();
        var columns = new List<string>();
        foreach (var item in items)
        {
            foreach (var prop in item.Properties())
            {
                if (!prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase)
                    && !prop.Name.Equals("pk", StringComparison.OrdinalIgnoreCase)
                    && !columns.Contains(prop.Name))
                {
                    columns.Add(prop.Name);
                }
            }
        }

        var cols = columns.ToArray();
        var rows = items.Select(i => cols.Select(c => (object?)i[c]?.ToObject<object>()).ToArray()).ToList();
        return new CosmosDbDataReader(cols, rows);
    }

    // Reduce-index query: doc ← bridge → index. Resolve in three steps — matching index Ids, then the
    // bridge rows linking them to documents, then the document ids.
    private async Task<List<long>> GatherReduceDocumentIdsAsync(string sql, CancellationToken cancellationToken)
    {
        var bridge = Regex.Match(sql, @"join\s+\[([^\]]+)\]\s+as\s+\w+\s+on\s+\w+\.\[DocumentId\]\s*=\s*\[[^\]]+\]\.\[Id\]", RegexOptions.IgnoreCase);
        var index = Regex.Match(sql, @"join\s+\[([^\]]+)\]\s+as\s+\w+\s+on\s+\w+\.\[Id\]\s*=\s*\w+\.\[(\w+)\]", RegexOptions.IgnoreCase);
        if (!bridge.Success || !index.Success)
        {
            throw new NotSupportedException($"Unsupported reduce query: {CommandText}");
        }

        var bridgeTable = bridge.Groups[1].Value;
        var indexTable = index.Groups[1].Value;
        var bridgeForeignKey = index.Groups[2].Value;

        var where = ExtractWhere(sql);
        var indexWhere = string.IsNullOrWhiteSpace(where) ? string.Empty : " AND " + TranslateWhere(StripDocTypePredicate(where!));

        // 1. matching index rows
        var indexQuery = new QueryDefinition("SELECT VALUE c.Id FROM c WHERE c.pk = @pk" + indexWhere).WithParameter("@pk", indexTable);
        foreach (DbParameter p in _parameters)
        {
            indexQuery = indexQuery.WithParameter("@" + p.ParameterName.TrimStart('@'), p.Value is DBNull ? null : p.Value);
        }

        var indexIds = new List<long>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<long>(indexQuery,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(indexTable) }))
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
            $"SELECT VALUE c.DocumentId FROM c WHERE c.pk = @pk AND c[\"{bridgeForeignKey}\"] IN ({string.Join(", ", indexIds)})")
            .WithParameter("@pk", bridgeTable);

        var documentIds = new List<long>();
        using (var iterator = CosmosContainer.GetItemQueryIterator<long>(bridgeQuery,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(bridgeTable) }))
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
                var resp = await CosmosContainer.ReadItemAsync<JObject>($"{documentTable}:{docId}", new PartitionKey(documentTable), cancellationToken: cancellationToken);
                rows.Add(ToRow(resp.Resource));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // document missing
            }
        }

        return new CosmosDbDataReader(DocumentColumns, rows);
    }

    private async Task<long?> MaxIdAsync(string table, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT VALUE MAX(c.Id) FROM c WHERE c.pk = @pk").WithParameter("@pk", table);
        using var iterator = CosmosContainer.GetItemQueryIterator<long?>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(table) });

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

    private static JToken ToToken(object? value) => value is null ? JValue.CreateNull() : JToken.FromObject(value);

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
