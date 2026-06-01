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

    private Container Container => _connection.CosmosContainer;

    // ---- async (primary) path, used by Dapper via QueryAsync/ExecuteAsync ----

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var sql = CommandText.TrimStart();

        if (StartsWith(sql, "insert") || StartsWith(sql, "update"))
        {
            var table = ExtractTable(sql);
            var id = Convert.ToInt64(Param("Id"));
            var item = new JObject
            {
                ["id"] = $"{table}:{id}",
                ["pk"] = table,
                ["Id"] = id,
                ["Type"] = ToToken(Param("Type")),
                ["Content"] = ToToken(Param("Content")),
                ["Version"] = ToToken(Param("Version")),
            };

            await Container.UpsertItemAsync(item, new PartitionKey(table), cancellationToken: cancellationToken);
            return 1;
        }

        if (StartsWith(sql, "delete"))
        {
            var table = ExtractTable(sql);
            var id = Convert.ToInt64(Param("Id"));
            try
            {
                await Container.DeleteItemAsync<JObject>($"{table}:{id}", new PartitionKey(table), cancellationToken: cancellationToken);
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
            var query = new QueryDefinition("SELECT VALUE MAX(c.Id) FROM c WHERE c.pk = @pk")
                .WithParameter("@pk", table);

            using var iterator = Container.GetItemQueryIterator<long?>(query,
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

        throw new NotSupportedException($"Unsupported scalar statement: {CommandText}");
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var sql = CommandText;

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
                    var resp = await Container.ReadItemAsync<JObject>($"{table}:{id}", new PartitionKey(table), cancellationToken: cancellationToken);
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

    private static object?[] ToRow(JObject item) =>
    [
        item["Id"]?.ToObject<long>(),
        item["Type"]?.ToObject<string>(),
        item["Content"]?.ToObject<string>(),
        item["Version"]?.ToObject<long>(),
    ];

    private static JToken ToToken(object? value) => value is null ? JValue.CreateNull() : JToken.FromObject(value);

    private object? Param(string name)
    {
        foreach (DbParameter p in _parameters)
        {
            var n = p.ParameterName.TrimStart('@');
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value is DBNull ? null : p.Value;
            }
        }

        throw new InvalidOperationException($"Parameter '{name}' not found for: {CommandText}");
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
