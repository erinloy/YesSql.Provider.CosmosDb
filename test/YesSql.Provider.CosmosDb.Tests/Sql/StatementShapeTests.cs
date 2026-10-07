using YesSql.Provider.CosmosDb.Internal;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

// The golden file statement-shapes.tsv holds what the regex implementation extracted from each statement of the
// corpus: how it was routed, its tables and joins, predicate, ordering and paging. The parser-based code has to find
// the same things. Only the fields a route uses are compared, because the regexes also produced values that no code
// read (the projection of a count query, for example).
public class StatementShapeTests
{
    private static string[] Corpus() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Sql", "statements.sql")).Where(l => l.Length > 0).ToArray();

    private static Dictionary<int, string> Golden()
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Sql", "statement-shapes.tsv"))
            .Where(l => l.Length > 0)
            .ToDictionary(l => int.Parse(l[..l.IndexOf('\t')]), l => l[(l.IndexOf('\t') + 1)..]);

    // The fields each reader route reads, besides the ones every select needs.
    private static readonly Dictionary<string, string[]> RouteFields = new()
    {
        ["CountJoinRow"] = ["indexJoins", "linkJoin", "reduce", "typeParameter"],
        ["ReduceJoin"] = ["indexJoins", "reduce", "order", "offset", "limit"],
        ["MultiIndexJoin"] = ["indexJoins", "order", "offset", "limit"],
        ["IndexJoin"] = ["indexJoins", "linkJoin", "typeParameter", "order", "offset", "limit"],
        ["DocumentsByJoin"] = ["order", "orderClause", "offset", "limit", "typeLiteral", "projection"],
        ["CountRow"] = [],
        ["DatePart"] = ["datePart"],
        ["DocumentsById"] = [],
        ["Documents"] = ["order", "orderClause", "offset", "limit", "typeLiteral", "projection"],
        ["IndexRows"] = ["order", "orderClause", "offset", "limit"],
    };

    private static readonly string[] AlwaysCompared = ["kind", "reader", "from", "where", "table", "replace", "versionCheck", "allowNull", "columns", "rename"];

    // The fields of a statement to compare, chosen from what the regex implementation recorded. A scalar route is
    // compared when the statement is run as a scalar, which is a count or a MAX seed; the regexes also took the MAX of
    // an ORDER BY alias inside an index query for a seed, which nothing runs.
    private static HashSet<string> Compared(string expected)
    {
        var fields = expected.Split('\t').Select(f => f.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty);
        var compared = AlwaysCompared.ToHashSet();
        if (!fields.TryGetValue("reader", out var reader))
        {
            return compared;
        }

        if (fields["scalar"] == "MaxId" && fields["join"] == "0")
        {
            compared.Add("scalar");
            return compared;
        }

        if (fields["scalar"] is "CountItems" or "CountJoin")
        {
            compared.Add("scalar");
        }

        if (RouteFields.TryGetValue(reader, out var extra))
        {
            compared.UnionWith(extra);
        }

        return compared;
    }

    private static string Canonical(string line, HashSet<string> compared)
        => string.Join('\t', line.Split('\t').Where(f => compared.Contains(f[..Math.Max(0, f.IndexOf('='))])));

    private static string Describe(string sql)
    {
        var statement = SqlParser.ParseStatement(sql);
        var fields = new List<string>();
        switch (statement)
        {
            case SelectStatement select:
                var shape = SelectShape.Of(select, sql);
                fields.Add("kind=select");
                fields.Add("reader=" + shape.Reader);
                fields.Add("scalar=" + (shape.Scalar?.ToString() ?? "Unsupported"));
                fields.Add("from=" + shape.FromTable);
                fields.Add("where=" + (shape.Where is null ? string.Empty : SqlPrinter.Print(shape.Where)));
                fields.Add("order=" + Attempt(() => string.Join(",", shape.Order.Select(o => o.Column + (o.Descending ? ":D" : ":A")))));
                fields.Add("orderClause=" + Attempt(() => CosmosDbCommand.OrderByClause(shape.Order)));
                fields.Add("offset=" + shape.Offset);
                fields.Add("limit=" + (shape.Limit?.ToString() ?? "none"));
                fields.Add("indexJoins=" + string.Join(",", shape.IndexJoins.Select(j => j.Table + ":" + j.Alias)));
                fields.Add("linkJoin=" + (shape.LinkJoin is { } link ? link.Table + ":" + link.Alias : string.Empty));
                fields.Add("reduce=" + (shape.Reduce is { } reduce
                    ? $"{reduce.IndexTable}:{reduce.IndexAlias}:{reduce.BridgeAlias}:{reduce.BridgeColumn}/{reduce.BridgeTable}"
                    : string.Empty));
                fields.Add("typeParameter=" + SqlTree.DocumentTypeParameter(shape.Where));
                fields.Add("typeLiteral=" + SqlTree.TypeLiteral(shape.Where));
                fields.Add("projection=" + Attempt(() => shape.Projection is null ? "*" : string.Join(",", shape.Projection)));
                fields.Add("datePart=" + (shape.DatePart is { } date ? date.Part + ":" + date.Column : string.Empty));
                break;

            case InsertStatement insert:
                fields.Add("kind=insert");
                fields.Add("table=" + insert.Table);
                fields.Add("columns=" + string.Join(",", insert.Columns));
                break;

            case UpdateStatement update:
                fields.Add("kind=update");
                fields.Add("table=" + update.Table);
                fields.Add("replace=" + (update.Assignments[0].Value is FunctionExpr { Name: var name } && name.Equals("replace", StringComparison.OrdinalIgnoreCase)
                    ? update.Table + ":" + update.Assignments[0].Column
                    : string.Empty));
                fields.Add("versionCheck=" + SqlTree.VersionCheck(update.Where));
                fields.Add("allowNull=" + (SqlTree.AllowsNullVersion(update.Where) ? "1" : "0"));
                fields.Add("where=" + (update.Where is null ? string.Empty : SqlPrinter.Print(update.Where)));
                break;

            case DeleteStatement delete:
                fields.Add("kind=delete");
                fields.Add("table=" + delete.Table);
                fields.Add("where=" + (delete.Where is null ? string.Empty : SqlPrinter.Print(delete.Where)));
                break;

            case RenameColumnStatement rename:
                fields.Add("kind=renamecolumn");
                fields.Add("table=" + rename.Table);
                fields.Add("rename=" + $"{rename.Table}:{rename.From}:{rename.To}");
                break;
        }

        return string.Join('\t', fields);
    }

    // An order or projection the provider refuses is written as "!" so the comparison shows it.
    private static string Attempt(Func<string> text)
    {
        try
        {
            return text();
        }
        catch (SqlSyntaxException)
        {
            return "!unsupported";
        }
    }

    [Fact]
    public void Every_statement_is_routed_and_read_the_way_the_regex_implementation_did()
    {
        var corpus = Corpus();
        var differences = new List<string>();
        var compared = 0;

        foreach (var (index, expected) in Golden())
        {
            if (expected == "REJECTED")
            {
                continue;
            }

            var actual = Describe(corpus[index]);
            compared++;
            var fields = Compared(expected);
            if (Canonical(actual, fields) != Canonical(expected, fields))
            {
                var left = Canonical(expected, fields).Split('\t');
                var right = Canonical(actual, fields).Split('\t');
                var changed = left.Zip(right, (e, a) => e == a ? null : $"   expected {e}\n   actual   {a}")
                    .Where(x => x is not null);
                differences.Add($"{corpus[index]}\n{string.Join("\n", changed)}");
            }
        }

        Assert.True(compared > 250, $"only {compared} statements were compared");
        Assert.True(differences.Count == 0, $"{differences.Count} statements differ:\n\n{string.Join("\n\n", differences)}");
    }
}
