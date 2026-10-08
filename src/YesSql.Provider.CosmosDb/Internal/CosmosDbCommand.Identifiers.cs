using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

// The Identifiers table of YesSql's DbBlockIdGenerator (Configuration.UseBlockIdGenerator): one row for each collection,
// with the columns [dimension] and [nextval]. A process leases a block of ids by reading its collection's row and then
// writing nextval + blockSize back, only if nextval is still what it read. That is a conditional write, which Cosmos has
// for an item as an ETag, so the leases of several processes never overlap and ids stay unique across processes.
//
// The row has no numeric Id, so it does not take the generic path. Its item is { id: "<table>:<dimension>", dimension, nextval }.
internal sealed partial class CosmosDbCommand
{
    // The id of the item for the row the command's @dimension names. A dimension is a collection name, which may be empty,
    // and may hold characters that an item id cannot.
    private string IdentifierItemId(string table, SqlExpr dimension)
        => $"{table}:{Uri.EscapeDataString(Convert.ToString(ValueToken(dimension).ToObject<object>(), CultureInfo.InvariantCulture) ?? string.Empty)}";

    // SELECT [nextval] FROM [Identifiers] WHERE [dimension] = @dimension: the block the next lease starts at, or null
    // when the collection has no row yet.
    private async Task<object?> ReadIdentifierAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var table = shape.RequiredFromTable;
        var dimension = shape.IdentifierDimension!;

        try
        {
            var row = await CosmosContainer.ReadItemAsync<JObject>(IdentifierItemId(table, dimension), PartitionKeyFor(table), cancellationToken: cancellationToken);
            return row.Resource["nextval"]?.ToObject<long>();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // INSERT INTO [Identifiers] ([dimension], [nextval]) VALUES (@dimension, @nextval). Two processes that start together both
    // insert, and the second fails with a conflict, as it would with the primary key of the relational table. The generator
    // reads the row again.
    private async Task<int> InsertIdentifierAsync(InsertStatement insert, CancellationToken cancellationToken)
    {
        var dimensionAt = insert.Columns.ToList().FindIndex(column => column.Equals("dimension", StringComparison.OrdinalIgnoreCase));
        if (dimensionAt < 0 || insert.Columns.Count != 2)
        {
            throw new SqlSyntaxException("An insert into the Identifiers table supports the columns [dimension] and [nextval]", CommandText);
        }

        var item = new JObject { ["id"] = IdentifierItemId(insert.Table, insert.Values[dimensionAt]) };
        for (var i = 0; i < insert.Columns.Count; i++)
        {
            item[insert.Columns[i]] = ValueToken(insert.Values[i]);
        }

        WithPartition(item, insert.Table);
        await CosmosContainer.CreateItemAsync(item, PartitionKeyFor(insert.Table), cancellationToken: cancellationToken);
        Undo?.Record(item["id"]!.ToString(), PkValue(insert.Table), null);
        return 1;
    }

    // UPDATE [Identifiers] SET [nextval] = @new WHERE [nextval] = @previous AND [dimension] = @dimension. It changes the row
    // only if nextval is still @previous, and says whether it did: 1 for the process that got the block, 0 for one that lost
    // the race to another and has to read again.
    private async Task<int> UpdateIdentifierAsync(UpdateStatement update, CancellationToken cancellationToken)
    {
        var compared = SqlTree.EqualityOperands(update.Where);
        if (update.Assignments is not [{ Column: var column, Value: var next }]
            || !column.Equals("nextval", StringComparison.OrdinalIgnoreCase)
            || compared is null
            || !compared.TryGetValue("nextval", out var previous)
            || !compared.TryGetValue("dimension", out var dimension))
        {
            throw new SqlSyntaxException("An update of the Identifiers table supports SET [nextval] = x WHERE [nextval] = y AND [dimension] = z", CommandText);
        }

        var table = update.Table;
        var id = IdentifierItemId(table, dimension);

        JObject item;
        string etag;
        try
        {
            var row = await CosmosContainer.ReadItemAsync<JObject>(id, PartitionKeyFor(table), cancellationToken: cancellationToken);
            item = row.Resource;
            etag = row.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        if (item["nextval"]?.ToObject<long>() != ValueToken(previous).ToObject<long>())
        {
            return 0;
        }

        var prior = (JObject)item.DeepClone();
        item["nextval"] = ValueToken(next);
        try
        {
            await CosmosContainer.ReplaceItemAsync(item, id, PartitionKeyFor(table), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return 0;
        }

        Undo?.Record(id, PkValue(table), prior);
        return 1;
    }
}
