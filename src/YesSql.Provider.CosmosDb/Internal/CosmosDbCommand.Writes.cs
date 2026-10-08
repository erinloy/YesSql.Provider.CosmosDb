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

// Statements that change data: INSERT, UPDATE, DELETE and the renamecolumn schema command. The statement is parsed, and
// values come from the command's parameters where the statement refers to them.
internal sealed partial class CosmosDbCommand
{
    private async Task<int> ExecuteNonQueryCoreAsync(CancellationToken cancellationToken)
    {
        _derivedParameters.Clear();

        switch (SqlParser.ParseStatement(CommandText))
        {
            case RenameColumnStatement rename:
                await CompleteWritesAsync();
                return await RenameColumnAsync(rename, cancellationToken);
            case InsertStatement insert when SelectShape.IsIdentifierTable(insert.Table):
                return await InsertIdentifierAsync(insert, cancellationToken);
            case InsertStatement insert:
                return await InsertAsync(insert, cancellationToken);
            case UpdateStatement update when SelectShape.IsIdentifierTable(update.Table):
                return await UpdateIdentifierAsync(update, cancellationToken);
            case UpdateStatement update:
                return await UpdateAsync(update, cancellationToken);
            case DeleteStatement delete:
                await CompleteWritesAsync();
                return await DeleteAsync(delete, cancellationToken);
            default:
                throw new NotSupportedException($"Unsupported non-query statement: {CommandText}");
        }
    }

    // RenameColumn DDL (emitted by the schema interpreter): rewrite the field on every row in the partition. Cosmos is
    // schemaless, so a column rename is a data rewrite, not metadata.
    private async Task<int> RenameColumnAsync(RenameColumnStatement rename, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE " + Scoped(rename.Table)).WithParameter("@pk", PkValue(rename.Table));
        var renamed = 0;
        using var iterator = CosmosContainer.GetItemQueryIterator<JObject>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(rename.Table) });
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                if (item.Property(rename.From) is null)
                {
                    continue;
                }

                item[rename.To] = item[rename.From];
                item.Remove(rename.From);
                await CosmosContainer.UpsertItemAsync(item, PartitionKeyFor(rename.Table), cancellationToken: cancellationToken);
                renamed++;
            }
        }

        return renamed;
    }

    private async Task<int> InsertAsync(InsertStatement insert, CancellationToken cancellationToken)
    {
        // Reduce index bridge row (the link between an index row and a document). The columns (e.g. [ArticlesByDayId], [DocumentId]) don't match
        // the parameter names (@Id, @DocumentId), so each column takes the value written at its position. Composite
        // key (<table>:<indexFk>:<documentId>), because many rows share an index id.
        if (TryParam("DocumentId", out var bridgeDocumentId) && !TryParam("Type", out _) && !TryParam("Content", out _))
        {
            var bridge = new JObject();
            for (var i = 0; i < insert.Columns.Count; i++)
            {
                bridge[insert.Columns[i]] = ValueToken(insert.Values[i]);
            }

            var indexForeignKey = bridge[insert.Columns[0]]?.ToString();
            bridge["id"] = $"{insert.Table}:{indexForeignKey}:{bridgeDocumentId}";
            WithPartition(bridge, insert.Table);

            await CreateItemAsync(bridge, insert.Table, cancellationToken);
            return 1;
        }

        // The row Id is @Id when given. An INSERT into an identity table (e.g. Orchard's [RecordIndexingTask]) carries no
        // @Id. Cosmos has no auto-increment, so the id comes from the table's counter.
        var id = TryParam("Id", out var idParameter) && idParameter is not null
            ? Convert.ToInt64(idParameter)
            : await NextSequenceAsync(insert.Table, cancellationToken);

        var item = new JObject { ["id"] = $"{insert.Table}:{id}", ["Id"] = id };
        PatchFromParameters(item);

        // Literal values (no parameter), e.g. "INSERT INTO [T] ([C1]) VALUES ('v')". Parameterised positions are
        // already in the item.
        for (var i = 0; i < insert.Columns.Count; i++)
        {
            if (insert.Values[i] is not ParamRef && !insert.Columns[i].Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                item[insert.Columns[i]] = ValueToken(insert.Values[i]);
            }
        }

        WithPartition(item, insert.Table);
        await CreateItemAsync(item, insert.Table, cancellationToken);
        return 1;
    }

    private async Task<int> UpdateAsync(UpdateStatement update, CancellationToken cancellationToken)
    {
        // Bulk content rewrite: UPDATE [<table>] SET [<col>] = REPLACE([<col>], <from>, <to>) [WHERE <pred>].
        // Orchard Core issues this to rename serialized $type names in stored documents. It has no @Id, so it cannot use
        // the single-row UPDATE below.
        if (update.Assignments.Any(assignment => assignment.Value is FunctionExpr function
            && function.Name.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)))
        {
            await CompleteWritesAsync();
            return await ReplaceContentAsync(update, cancellationToken);
        }

        var table = update.Table;

        // The row is the one WHERE [Id] = x names, and a version check is the only other condition understood. Any other
        // condition is refused, because an update that ignored it would change rows the SQL did not select.
        var condition = SqlTree.UpdateCondition(update.Where)
            ?? throw new SqlSyntaxException("UPDATE is supported for one row, WHERE [Id] = x, with an optional [Version] check", CommandText);
        var id = Convert.ToInt64(KeyValue(condition.Key));

        // Only a write to this same item has to land before it is read.
        await CompleteWriteAsync($"{table}:{id}");

        JObject item;
        string etag;
        try
        {
            var existing = await CosmosContainer.ReadItemAsync<JObject>($"{table}:{id}", PartitionKeyFor(table), cancellationToken: cancellationToken);
            item = existing.Resource;
            etag = existing.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // An UPDATE of a row that is not there changes no row, as in a relational database.
            return 0;
        }

        // Optimistic concurrency: a checked update compares the stored version. YesSql throws ConcurrencyException when the
        // affected count is not 1, so a mismatch returns 0.
        var checkVersion = condition.Version;
        if (checkVersion is { } expected)
        {
            var current = item["Version"];
            var currentVersion = current is null || current.Type == JTokenType.Null ? (long?)null : current.ToObject<long>();
            if (!(currentVersion == expected || (condition.AllowsNullVersion && currentVersion is null)))
            {
                return 0;
            }
        }

        // Snapshot the prior state, which is what rollback restores, and set the columns of the SET clause.
        var prior = (JObject)item.DeepClone();
        foreach (var assignment in update.Assignments)
        {
            if (assignment.Column.Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlSyntaxException("UPDATE cannot change the [Id] of a row", CommandText);
            }

            item[assignment.Column] = ValueToken(assignment.Value);
        }

        WithPartition(item, table);

        // Version-checked updates use an ETag-conditional replace so a concurrent write between the read and the write
        // is also detected (412 means the row changed, which counts as a failed check).
        if (checkVersion is not null)
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
            // The write is not checked, so it counts as one row whatever its outcome. A failure is thrown by the next
            // command that waits for the writes in flight, or by commit.
            Undo?.Record($"{table}:{id}", PkValue(table), prior);
            await ReplaceItemAsync(item, table, cancellationToken);
            return 1;
        }

        Undo?.Record($"{table}:{id}", PkValue(table), prior);
        return 1;
    }

    // The value of a key operand: a bound @parameter or a whole number.
    private object KeyValue(SqlExpr key)
        => key switch
        {
            ParamRef parameter => Param(parameter.Name) ?? throw new SqlSyntaxException($"The key parameter @{parameter.Name} is null", CommandText),
            LiteralExpr { Value: long number } => number,
            _ => throw new SqlSyntaxException("A key has to be a parameter or a whole number", CommandText),
        };

    // Query the matching items, string-replace the column on each, and write them back. Document.Content is stored as a
    // JSON string, so REPLACE is a plain string replace; the arguments may be 'literals' or @parameters.
    private async Task<int> ReplaceContentAsync(UpdateStatement update, CancellationToken cancellationToken)
    {
        if (update.Assignments.Count != 1
            || update.Assignments[0].Value is not FunctionExpr { Arguments: [ColumnRef source, var fromArgument, var toArgument] }
            || !source.Name.Equals(update.Assignments[0].Column, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlSyntaxException("Only UPDATE [T] SET [Col] = REPLACE([Col], from, to) [WHERE ...] is supported for REPLACE", CommandText);
        }

        var table = update.Table;
        var column = update.Assignments[0].Column;
        var fromValue = StringValue(fromArgument);
        var toValue = StringValue(toArgument) ?? string.Empty;

        // SQL REPLACE with a NULL search value yields NULL, which would erase the column. Fail rather than rewrite
        // content from what is almost certainly a missing parameter.
        if (fromValue is null)
        {
            throw new NotSupportedException($"REPLACE with a NULL search value is not supported: {CommandText}");
        }

        // An empty search string matches nothing in SQL REPLACE, so there is nothing to rewrite.
        if (fromValue.Length == 0)
        {
            return 0;
        }

        var cosmosWhere = update.Where is null ? string.Empty : " AND " + await WriteWhereAsync(update.Where, cancellationToken);
        var query = BindParameters(new QueryDefinition("SELECT * FROM c WHERE " + Scoped(table) + cosmosWhere)
            .WithParameter("@pk", PkValue(table)));

        var matches = await ReadAllAsync<JObject>(query, table, cancellationToken);
        var replaced = 0;
        foreach (var item in matches)
        {
            if (item[column]?.Type != JTokenType.String)
            {
                continue;
            }

            var current = item[column]!.ToObject<string>()!;
            if (!current.Contains(fromValue, StringComparison.Ordinal))
            {
                continue;
            }

            var prior = (JObject)item.DeepClone();
            item[column] = current.Replace(fromValue, toValue, StringComparison.Ordinal);
            await CosmosContainer.UpsertItemAsync(item, PartitionKeyFor(table), cancellationToken: cancellationToken);
            Undo?.Record(item["id"]!.ToString(), PkValue(table), prior);
            replaced++;
        }

        return replaced;
    }

    // General delete: query items in the partition matching the WHERE (by [Id] for documents, by [DocumentId] for map
    // indexes, by composite key for reduce bridge rows), then delete each.
    private async Task<int> DeleteAsync(DeleteStatement delete, CancellationToken cancellationToken)
    {
        var table = delete.Table;
        var cosmosWhere = delete.Where is null ? string.Empty : " AND " + await WriteWhereAsync(delete.Where, cancellationToken);

        // SELECT * (not just id) so the full items can be restored on rollback.
        var query = BindParameters(new QueryDefinition("SELECT * FROM c WHERE " + Scoped(table) + cosmosWhere)
            .WithParameter("@pk", PkValue(table)));

        var items = await ReadAllAsync<JObject>(query, table, cancellationToken);
        foreach (var item in items)
        {
            var id = item["id"]!.ToString();
            Undo?.Record(id, PkValue(table), item); // restore the deleted item on rollback
            await DeleteItemAsync(id, table, cancellationToken);
        }

        return items.Count;
    }

    // Stores every command parameter as a field, except the key. Documents carry Type/Content/Version and indexes their
    // own columns, and YesSql names each parameter after its column.
    private void PatchFromParameters(JObject item)
    {
        foreach (DbParameter p in _parameters)
        {
            var name = p.ParameterName.TrimStart('@');
            if (!name.Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                item[name] = ToToken(p.Value is DBNull ? null : p.Value);
            }
        }
    }

    // The value of a statement's value expression: a bound @parameter or a literal.
    private JToken ValueToken(SqlExpr value)
        => value switch
        {
            ParamRef parameter => ToToken(Param(parameter.Name)),
            LiteralExpr { Value: null } => JValue.CreateNull(),
            LiteralExpr { Value: string text } => new JValue(text),
            LiteralExpr { Value: bool flag } => new JValue(flag),
            LiteralExpr { Value: long whole } => new JValue(whole),
            LiteralExpr { Value: decimal number } => new JValue((double)number),
            NegateExpr { Operand: LiteralExpr { Value: long whole } } => new JValue(-whole),
            NegateExpr { Operand: LiteralExpr { Value: decimal number } } => new JValue(-(double)number),
            _ => throw new SqlSyntaxException("INSERT supports parameters and literal values", CommandText),
        };

    // A string literal or bound @parameter as a string (null for NULL or a null parameter). Used by the REPLACE update.
    private string? StringValue(SqlExpr value)
        => value switch
        {
            LiteralExpr { Value: null } => null,
            LiteralExpr { Value: string text } => text,
            LiteralExpr { Value: IFormattable number } => number.ToString(null, CultureInfo.InvariantCulture),
            ParamRef parameter => Param(parameter.Name)?.ToString(),
            _ => throw new SqlSyntaxException("REPLACE arguments must be literals or parameters", CommandText),
        };
}
