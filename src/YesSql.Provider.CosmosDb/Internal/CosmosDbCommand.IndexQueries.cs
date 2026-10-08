using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

// Queries over one map index that Cosmos can answer itself. The general path (GatherDocumentIdsAsync) reads every
// matching index row to the client, then removes duplicate documents, orders and pages there, so its request unit cost
// grows with the number of rows that match. These shapes need only the distinct document ids, in document id order, so
// Cosmos can count them or return one page of them.
internal sealed partial class CosmosDbCommand
{
    // True for a query over a single index table that is ordered by document id or not at all, and that does not filter
    // on the document's type. A type filter is applied to the gathered ids afterwards, so it cannot be paged in Cosmos.
    private static bool CanAnswerInCosmos(SelectShape shape)
        => shape.LinkJoin is not null
           && !shape.HasReduceJoin
           && shape.IndexJoins.Select(join => join.Table).Distinct().Count() < 2
           && SqlTree.DocumentTypeParameter(shape.Where) is null
           && shape.Order.All(term => term.Column.Equals("DocumentId", StringComparison.OrdinalIgnoreCase));

    private async Task<string> IndexRowsFilterAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var predicate = SqlTree.WithoutDocumentTypePredicate(shape.Where);
        return Scoped(shape.LinkJoin!.Table) + (predicate is null ? string.Empty : " AND " + await WriteWhereAsync(predicate, cancellationToken));
    }

    /// <summary>
    /// The page of distinct document ids that a paged query selects, in document id order, or null when the query is
    /// not one Cosmos can page (the caller then gathers every id).
    /// </summary>
    private async Task<List<long>?> TryPageDocumentIdsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        var paging = OffsetLimitClause(shape);
        if (paging.Length == 0 || !CanAnswerInCosmos(shape))
        {
            return null;
        }

        var table = shape.LinkJoin!.Table;
        var descending = shape.Order.Count > 0 && shape.Order[0].Descending;
        var queryDef = new QueryDefinition(
                $"SELECT DISTINCT VALUE {CosmosExpressionWriter.Property("DocumentId")} FROM c WHERE " + await IndexRowsFilterAsync(shape, cancellationToken)
                + $" ORDER BY {CosmosExpressionWriter.Property("DocumentId")}" + (descending ? " DESC" : string.Empty) + paging)
            .WithParameter("@pk", PkValue(table));
        queryDef = BindParameters(queryDef);

        var ids = await ReadAllAsync<long>(queryDef, table, cancellationToken);

        return ids;
    }

    /// <summary>The number of distinct documents an unpaged query selects, or null when Cosmos cannot count them.</summary>
    private async Task<long?> TryCountDocumentsAsync(SelectShape shape, CancellationToken cancellationToken)
    {
        if (OffsetLimitClause(shape).Length > 0 || !CanAnswerInCosmos(shape))
        {
            return null;
        }

        return await CountDistinctDocumentIdsAsync(shape.LinkJoin!.Table, await IndexRowsFilterAsync(shape, cancellationToken), cancellationToken);
    }

}
