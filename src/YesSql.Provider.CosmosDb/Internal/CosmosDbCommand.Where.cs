using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

// Translation of a WHERE clause into a Cosmos predicate: the text is parsed, subqueries are run, and the tree is written
// out with column names as properties and string literals as query parameters.
internal sealed partial class CosmosDbCommand
{
    /// <summary>
    /// Writes a parsed predicate as a Cosmos predicate. <c>IN (SELECT ...)</c> subqueries are run first and their
    /// values passed as query parameters, and string literals become query parameters too, so neither can change the
    /// query. The parameters are added to the ones bound by <see cref="BindParameters"/>.
    /// </summary>
    internal async Task<string> WriteWhereAsync(SqlExpr predicate, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<InSubqueryExpr, string>(ReferenceEqualityComparer.Instance);
        foreach (var subquery in SqlTree.Subqueries(predicate))
        {
            resolved[subquery] = await RunSubqueryAsync(subquery, cancellationToken);
        }

        var writer = new CosmosExpressionWriter(subquery => resolved[subquery], BindLiteral, DateParameterNames());
        return writer.Write(predicate);
    }

    // Runs "x IN (SELECT col FROM [table] AS alias WHERE ...)" against the subquery's table and returns the name of
    // the query parameter holding the values.
    private async Task<string> RunSubqueryAsync(InSubqueryExpr subquery, CancellationToken cancellationToken)
    {
        var query = subquery.Query;
        var innerWhere = query.Where is null ? string.Empty : " AND " + await WriteWhereAsync(query.Where, cancellationToken);

        var queryDef = new QueryDefinition(
                "SELECT VALUE " + CosmosExpressionWriter.Column(query.Column) + " FROM c WHERE " + Scoped(query.Table, "@__itbl") + innerWhere)
            .WithParameter("@__itbl", PkValue(query.Table));
        queryDef = BindParameters(queryDef);

        var values = new JArray();
        using (var iterator = CosmosContainer.GetItemQueryIterator<JToken>(queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(query.Table) }))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var value in await iterator.ReadNextAsync(cancellationToken))
                {
                    values.Add(value);
                }
            }
        }

        var name = "@__sq" + _derivedParameters.Count;
        _derivedParameters.Add((name, values));
        return name;
    }

    private string BindLiteral(string text)
    {
        var name = "@__lit" + _derivedParameters.Count;
        _derivedParameters.Add((name, text));
        return name;
    }

    // Parameters that hold dates; a column compared with one is compared by instant.
    private HashSet<string> DateParameterNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (DbParameter parameter in _parameters)
        {
            if (parameter.Value is DateTime or DateTimeOffset)
            {
                names.Add(parameter.ParameterName.TrimStart('@'));
            }
        }

        return names;
    }
}
