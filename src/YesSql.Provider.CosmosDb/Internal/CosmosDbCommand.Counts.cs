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

// COUNT statements: over one table, and over a join of documents to an index.
internal sealed partial class CosmosDbCommand
{
    // Counts for a COUNT over a join, in the three forms YesSql and its raw join API produce: COUNT(DISTINCT [Document].[Id])
    // over an index query, which counts documents; COUNT(1) over an inner join, which counts joined rows; and COUNT(1) over a
    // LEFT or RIGHT join. Shared by the scalar path and the reader path.
    private async Task<long> CountJoinAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        if (shape.HasOuterJoin)
        {
            return await CountOuterJoinAsync(shape, cancellationToken);
        }

        if (shape.CountsRows)
        {
            return await CountJoinedRowsAsync(shape, cancellationToken);
        }

        if (await TryCountDocumentsAsync(shape, cancellationToken) is { } counted)
        {
            return counted;
        }

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

    // COUNT(1) over an inner join of the document table to one map index: SQL counts the joined rows, which is one for each
    // index row, so a document with two rows counts twice. YesSql's own counts are COUNT(DISTINCT [Document].[Id]).
    private async Task<long> CountJoinedRowsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        if (!CanAnswerInCosmos(shape))
        {
            throw new NotSupportedException($"COUNT(1) over a join is supported for one map index, without a filter on the document type: {CommandText}");
        }

        var table = shape.LinkJoin!.Table;
        return await ReadCountAsync("SELECT VALUE COUNT(1) FROM c WHERE " + await IndexRowsFilterAsync(shape, cancellationToken), table, cancellationToken);
    }

    // COUNT(1) over [Document] LEFT or RIGHT JOIN [Index]. A RIGHT JOIN keeps every index row, so it counts them. A LEFT JOIN also
    // keeps a row for each document with no index row. Only that count is supported, over one index and without a WHERE
    // clause, which would turn the join into an inner join.
    private async Task<long> CountOuterJoinAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var join = shape.LinkJoin;
        if (join is null || !shape.CountsRows || shape.Where is not null || shape.HasReduceJoin || shape.NamedJoins.Count != 1)
        {
            throw new NotSupportedException($"A LEFT or RIGHT JOIN is supported only as COUNT(1) of a document table and one index table, without a WHERE clause: {CommandText}");
        }

        var rows = await CountItemsAsync(join.Table, null, cancellationToken);
        if (join.Kind == JoinKind.Right)
        {
            return rows;
        }

        var documentsWithRows = await CountDistinctDocumentIdsAsync(join.Table, Scoped(join.Table), cancellationToken);
        var documents = await CountItemsAsync(shape.RequiredFromTable, null, cancellationToken);
        return rows + (documents - documentsWithRows);
    }

    // Count items in a partition: SELECT count(...) FROM [<table>] [WHERE <predicate>]. Shared by the
    // scalar path (CountAsync) and the reader path (raw Dapper QueryFirstOrDefaultAsync<int>).
    private Task<long> CountItemsAsync(SelectShape shape, CancellationToken cancellationToken)
        => CountItemsAsync(shape.RequiredFromTable, shape.Where, cancellationToken);

    private async Task<long> CountItemsAsync(string table, SqlExpr? predicate, CancellationToken cancellationToken)
    {
        var cosmosWhere = predicate is null ? string.Empty : " AND " + await WriteWhereAsync(predicate, cancellationToken);
        return await ReadCountAsync("SELECT VALUE COUNT(1) FROM c WHERE " + Scoped(table) + cosmosWhere, table, cancellationToken);
    }

    /// <summary>The number of different documents that the index rows selected by <paramref name="filter"/> belong to.</summary>
    private Task<long> CountDistinctDocumentIdsAsync(string indexTable, string filter, CancellationToken cancellationToken)
        => ReadCountAsync(
            $"SELECT VALUE COUNT(1) FROM (SELECT DISTINCT VALUE {CosmosExpressionWriter.Property("DocumentId")} FROM c WHERE {filter})", indexTable, cancellationToken);

    // Runs a query that yields one number, with the command's parameters bound, and returns it (0 when it yields none).
    private async Task<long> ReadCountAsync(string query, string table, CancellationToken cancellationToken)
    {
        var definition = BindParameters(new QueryDefinition(query).WithParameter("@pk", PkValue(table)));
        return (await ReadAllAsync<long>(definition, table, cancellationToken)).FirstOrDefault();
    }

}
