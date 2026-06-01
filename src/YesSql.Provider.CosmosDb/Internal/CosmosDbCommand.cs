using System;
using System.Data;
using System.Data.Common;
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

            SetIfPresent(item, "Type");
            SetIfPresent(item, "Content");
            SetIfPresent(item, "Version");

            await CosmosContainer.UpsertItemAsync(item, new PartitionKey(table), cancellationToken: cancellationToken);
            return 1;
        }

        if (StartsWith(sql, "delete"))
        {
            var table = ExtractTable(sql);
            var id = Convert.ToInt64(Param("Id"));
            try
            {
                await CosmosContainer.DeleteItemAsync<JObject>($"{table}:{id}", new PartitionKey(table), cancellationToken: cancellationToken);
                return 1;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return 0;
            }
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
            var ids = await GatherDocumentIdsAsync(sql, cancellationToken);
            return ids.Count;
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

        // Index-joined query: SELECT [Document].* FROM [Document] INNER JOIN [Index] AS a ON
        // a.[DocumentId] = [Document].[Id] WHERE (<predicate on a.[Col]>) [LIMIT n].
        // Translated in two steps: query the index for matching DocumentIds, then point-read documents.
        if (Regex.IsMatch(sql, @"\bjoin\b", RegexOptions.IgnoreCase))
        {
            return await ExecuteIndexJoinQueryAsync(sql, cancellationToken);
        }

        // Document load by id(s): select * from [<table>] where [Id] = @Id  (single)
        // or  ... where [Id] in (@Ids1, @Ids2, …)  (Dapper-expanded list). In both shapes the only
        // parameters are id values, so we gather them and point-read each item.
        if (StartsWith(sql.TrimStart(), "select") && Regex.IsMatch(sql, @"\[id\]", RegexOptions.IgnoreCase))
        {
            var table = ExtractTableAfter(sql, "from");
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

        // WHERE predicate over index columns → Cosmos predicate (alias.[Col] → c["Col"]). Stop at the
        // next clause boundary (ListAsync wraps the lookup in "(… GROUP BY …) AS IndexQuery").
        var cosmosWhere = string.Empty;
        var whereMatch = Regex.Match(sql, @"\bwhere\b(.*?)(?:\bgroup\s+by\b|\border\s+by\b|\blimit\b|\boffset\b|\)\s*as\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (whereMatch.Success)
        {
            cosmosWhere = Regex.Replace(whereMatch.Groups[1].Value.Trim(), Regex.Escape(alias) + @"\.\[([^\]]+)\]", "c[\"$1\"]");
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

        var limitMatch = Regex.Match(sql, @"\blimit\s+(\d+)", RegexOptions.IgnoreCase);
        int? limit = limitMatch.Success ? int.Parse(limitMatch.Groups[1].Value) : null;

        var rows = new System.Collections.Generic.List<object?[]>();
        foreach (var docId in documentIds)
        {
            if (limit.HasValue && rows.Count >= limit.Value)
            {
                break;
            }

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

    private void SetIfPresent(JObject item, string name)
    {
        if (TryParam(name, out var value))
        {
            item[name] = ToToken(value);
        }
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
