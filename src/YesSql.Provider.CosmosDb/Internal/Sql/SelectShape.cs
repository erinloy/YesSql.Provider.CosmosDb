using System;
using System.Collections.Generic;
using System.Linq;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>How a <c>SELECT</c> executed as a scalar is run.</summary>
internal enum ScalarRoute
{
    /// <summary><c>SELECT MAX([Id]) FROM [table]</c>, which seeds an id generator.</summary>
    MaxId,

    /// <summary>A count over an index join, which counts the matching documents.</summary>
    CountJoin,

    /// <summary>A count over one table.</summary>
    CountItems,
}

/// <summary>How a <c>SELECT</c> executed as a reader is run, in the order the provider tests for them.</summary>
internal enum ReaderRoute
{
    /// <summary>A count over a join, returned as a one-row result.</summary>
    CountJoinRow,

    /// <summary>A document joined to a reduce index through its bridge table.</summary>
    ReduceJoin,

    /// <summary>A document joined to two or more different map index tables.</summary>
    MultiIndexJoin,

    /// <summary>A document joined to one map index table.</summary>
    IndexJoin,

    /// <summary>A document table joined to a derived table that has no index inside, the form of <c>Query&lt;T&gt;().ListAsync()</c>.</summary>
    DocumentsByJoin,

    /// <summary>A count over one table, returned as a one-row result.</summary>
    CountRow,

    /// <summary><c>SELECT DateTimePart("part", [Column]) FROM [table]</c>.</summary>
    DatePart,

    /// <summary>Documents loaded by key: <c>WHERE [Id] = @Id</c> or <c>[Id] IN (...)</c>.</summary>
    DocumentsById,

    /// <summary>Documents of a document table, filtered by type.</summary>
    Documents,

    /// <summary>The rows of an index table.</summary>
    IndexRows,
}

/// <summary>A join onto a named table: <c>JOIN [Table] AS Alias ON ...</c>.</summary>
internal sealed record NamedJoin(string Table, string Alias, SqlExpr On);

/// <summary>
/// The two joins that make up a reduce index: the index joined through the bridge table's foreign key
/// (<c>JOIN [Index] AS i ON i.[Id] = b.[IndexId]</c>) and the bridge table joined to the document
/// (<c>JOIN [Bridge] AS b ON b.[DocumentId] = [Document].[Id]</c>).
/// </summary>
internal sealed record ReduceJoin(string IndexTable, string IndexAlias, string BridgeAlias, string BridgeColumn, string? BridgeTable);

/// <summary>One column of an <c>ORDER BY</c>, with the select item aliases (<c>order_1</c>) resolved to the column they aggregate.</summary>
internal sealed record OrderColumn(string Column, bool Descending);

/// <summary>
/// What the provider needs to know about a parsed <c>SELECT</c> to run it: which table it reads, the joins that link
/// documents to index tables, and the predicate, ordering and paging, wherever YesSql put them.
/// </summary>
/// <remarks>
/// YesSql wraps an index query in a derived table (<c>SELECT [Document].* FROM [Document] INNER JOIN (SELECT ... ) AS
/// IndexQuery ON ...</c>). The joins, the predicate and the paging are inside it, and the outer statement repeats the
/// ordering. <see cref="Core"/> is that inner query, or the statement itself when there is none.
/// </remarks>
internal sealed class SelectShape
{
    /// <summary>Stands for the dialect's random order among the order columns.</summary>
    public const string RandomColumn = "$random";

    private readonly string _sql;
    private readonly List<JoinClause> _joins = new();
    private readonly List<NamedJoin> _namedJoins = new();
    private IReadOnlyList<OrderColumn>? _order;

    private SelectShape(SelectStatement statement, string sql)
    {
        Statement = statement;
        _sql = sql;

        var derived = (statement.From as DerivedSource)?.Query
            ?? statement.Joins.Select(join => join.Source).OfType<DerivedSource>().FirstOrDefault()?.Query;
        Core = derived ?? statement;

        CollectJoins(statement);
        foreach (var join in _joins)
        {
            if (join.Source is NamedSource { Alias: { } alias } named)
            {
                _namedJoins.Add(new NamedJoin(named.Table, alias, join.On));
            }
        }
    }

    public static SelectShape Of(SelectStatement statement, string sql) => new(statement, sql);

    public SelectStatement Statement { get; }

    /// <summary>The query that carries the joins, predicate and paging: the derived table of an index query, else the statement.</summary>
    public SelectStatement Core { get; }

    /// <summary>The table of the outermost <c>FROM</c>; for an index query this is the document table.</summary>
    public string? FromTable => (Statement.From as NamedSource)?.Table;

    public string RequiredFromTable
        => FromTable ?? throw Unsupported("The statement has no FROM [table]");

    /// <summary>The predicate, which is inside the derived table of an index query.</summary>
    public SqlExpr? Where => Core.Where ?? Statement.Where;

    public long Offset => Core.Offset ?? Statement.Offset ?? 0;

    public long? Limit => Core.Limit ?? Statement.Limit;

    /// <summary>True when the statement joins anything, including a derived table.</summary>
    public bool HasJoin => _joins.Count > 0;

    /// <summary>Every join in the statement, including those inside a derived table, in the order they are written.</summary>
    public IReadOnlyList<NamedJoin> NamedJoins => _namedJoins;

    /// <summary>True when a select item of the outermost query calls the function, for example <c>count(*)</c> or <c>MAX([Id])</c>.</summary>
    public bool HasFunction(string name)
        => Statement.Items.Any(item => item.Expression is FunctionExpr function
            && function.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Joins of an index table to a document written as <c>a.[DocumentId] = [Table].[Id]</c>, as <c>.With()</c> queries do.</summary>
    public IReadOnlyList<NamedJoin> IndexJoins
        => _namedJoins.Where(join => IsDocumentLink(join.On, requireTableQualifier: true)).ToList();

    /// <summary>
    /// The first join of an index table to a document, which may name the document by an alias
    /// (<c>a.[DocumentId] = d.[Id]</c>) as a hand-written join does.
    /// </summary>
    public NamedJoin? LinkJoin => _namedJoins.FirstOrDefault(join => IsDocumentLink(join.On, requireTableQualifier: false));

    /// <summary>True when a join reads <c>x.[Id] = y.[SomethingId]</c>, which is how an index row is joined to its bridge row.</summary>
    public bool HasReduceJoin => _joins.Any(join => IsReduceLink(join.On));

    /// <summary>The reduce index in the statement, or null when there is none.</summary>
    public ReduceJoin? Reduce
    {
        get
        {
            // The index is the first join of the form x.[Id] = bridge.[Column]. The bridge is the join named by that
            // qualifier, which has to be picked by alias because a query may also join plain map indexes whose link to
            // the document looks the same.
            foreach (var join in _namedJoins)
            {
                if (join.On is BinaryExpr { Operator: "=", Left: ColumnRef left, Right: ColumnRef right }
                    && IsAlias(left) && left.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
                    && IsAlias(right))
                {
                    var bridge = _namedJoins.FirstOrDefault(candidate =>
                        candidate.Alias.Equals(right.Qualifier, StringComparison.OrdinalIgnoreCase)
                        && IsDocumentLink(candidate.On, requireTableQualifier: false)
                        && candidate.On is BinaryExpr { Left: ColumnRef bridgeColumn }
                        && candidate.Alias.Equals(bridgeColumn.Qualifier, StringComparison.OrdinalIgnoreCase));
                    return new ReduceJoin(join.Table, join.Alias, right.Qualifier!, right.Name, bridge?.Table);
                }
            }

            return null;
        }
    }

    /// <summary>The columns the statement orders by, outermost <c>ORDER BY</c> first. Throws for an order the provider cannot apply.</summary>
    public IReadOnlyList<OrderColumn> Order => _order ??= ResolveOrder();

    public bool HasRandomOrder => Order.Any(column => column.Column == RandomColumn);

    /// <summary>The document columns a <c>SELECT</c> projects, or null for <c>*</c> or <c>alias.*</c>.</summary>
    public IReadOnlyList<string>? Projection
    {
        get
        {
            if (Statement.Items.Any(item => item.Expression is StarExpr))
            {
                return null;
            }

            var columns = new List<string>();
            foreach (var item in Statement.Items)
            {
                columns.Add(item.Expression is ColumnRef column
                    ? column.Name
                    : throw Unsupported("Only columns can be selected from a document table"));
            }

            return columns;
        }
    }

    /// <summary><c>SELECT DateTimePart("part", [Column]) FROM ...</c>, or null for any other select.</summary>
    public (string Part, string Column)? DatePart
        => Statement.Items.Count > 0
           && Statement.Items[0].Expression is FunctionExpr { Arguments: [QuotedNameExpr part, ColumnRef column] } function
           && function.Name.Equals("DateTimePart", StringComparison.OrdinalIgnoreCase)
            ? (part.Name, column.Name)
            : null;

    /// <summary>True for a table that holds YesSql documents, whose name ends in <c>Document</c>.</summary>
    public static bool IsDocumentTable(string table) => table.EndsWith("Document", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the statement is run when it is executed as a scalar, or null when it cannot be.</summary>
    public ScalarRoute? Scalar
    {
        get
        {
            if (HasFunction("max"))
            {
                return ScalarRoute.MaxId;
            }

            if (HasFunction("count"))
            {
                return HasJoin ? ScalarRoute.CountJoin : ScalarRoute.CountItems;
            }

            return null;
        }
    }

    /// <summary>How the statement is run when it is executed as a reader.</summary>
    public ReaderRoute Reader
    {
        get
        {
            var count = HasFunction("count");
            if (count && HasJoin)
            {
                return ReaderRoute.CountJoinRow;
            }

            if (HasReduceJoin)
            {
                return ReaderRoute.ReduceJoin;
            }

            if (IndexJoins.Select(join => join.Table).Distinct().Count() >= 2)
            {
                return ReaderRoute.MultiIndexJoin;
            }

            // An index join, flat or inside the derived table of an index query, is "JOIN [index] AS a ON a.[DocumentId] = ...".
            if (_namedJoins.Any(join => join.On is BinaryExpr { Left: ColumnRef left }
                && IsAlias(left) && left.Name.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)))
            {
                return ReaderRoute.IndexJoin;
            }

            if (HasJoin)
            {
                return ReaderRoute.DocumentsByJoin;
            }

            if (count)
            {
                return ReaderRoute.CountRow;
            }

            if (DatePart is not null)
            {
                return ReaderRoute.DatePart;
            }

            if (IsDocumentTable(RequiredFromTable))
            {
                return SqlTree.SelectsById(Where) ? ReaderRoute.DocumentsById : ReaderRoute.Documents;
            }

            return ReaderRoute.IndexRows;
        }
    }

    private SqlSyntaxException Unsupported(string message) => new(message, _sql);

    private void CollectJoins(SelectStatement select)
    {
        // The statement's own joins come after the ones inside the derived table that precedes them in the text.
        if (select.From is DerivedSource from)
        {
            CollectJoins(from.Query);
        }

        foreach (var join in select.Joins)
        {
            if (join.Source is DerivedSource derived)
            {
                CollectJoins(derived.Query);
            }

            _joins.Add(join);
        }
    }

    private static bool IsAlias(ColumnRef column) => column.Qualifier is not null && !column.QualifierIsTable;

    // a.[DocumentId] = x.[Id], where x is an alias or, when requireTableQualifier is set, a bracketed table.
    private static bool IsDocumentLink(SqlExpr on, bool requireTableQualifier)
        => on is BinaryExpr { Operator: "=", Left: ColumnRef left, Right: ColumnRef right }
           && IsAlias(left) && left.Name.Equals("DocumentId", StringComparison.OrdinalIgnoreCase)
           && right.Qualifier is not null && (right.QualifierIsTable || !requireTableQualifier)
           && right.Name.Equals("Id", StringComparison.OrdinalIgnoreCase);

    // x.[Id] = y.[SomethingId], with both qualifiers aliases.
    private static bool IsReduceLink(SqlExpr on)
        => on is BinaryExpr { Operator: "=", Left: ColumnRef left, Right: ColumnRef right }
           && IsAlias(left) && left.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
           && IsAlias(right) && right.Name.Length > 2 && right.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase);

    private static bool IsRandom(SqlExpr expression)
        => expression is FunctionExpr { Arguments.Count: 0 } function
           && function.Name.Equals(CosmosDbDialect.RandomFunction, StringComparison.OrdinalIgnoreCase);

    private static bool IsOrderAlias(string alias)
        => alias.Length > "order_".Length
           && alias.StartsWith("order_", StringComparison.OrdinalIgnoreCase)
           && alias.AsSpan("order_".Length).IndexOfAnyExceptInRange('0', '9') < 0;

    // The column an ORDER BY term names. A document column, as in MAX([Document].[Id]), orders the documents of an index
    // query by the id of the document, which the index rows carry as DocumentId; the other document columns are not
    // available to an index query.
    private string ResolveColumn(ColumnRef reference)
    {
        var ofDocument = reference.QualifierIsTable && FromTable is { } from
            && string.Equals(reference.Qualifier, from, StringComparison.OrdinalIgnoreCase);
        if (!ofDocument || (LinkJoin is null && !HasReduceJoin))
        {
            return reference.Name;
        }

        return reference.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
            ? "DocumentId"
            : throw Unsupported($"Ordering by [{reference.Qualifier}].[{reference.Name}] is not supported in a query over an index");
    }

    private IReadOnlyList<OrderColumn> ResolveOrder()
    {
        var terms = Statement.OrderBy.Count > 0 ? Statement.OrderBy : Core.OrderBy;
        if (terms.Count == 0)
        {
            return Array.Empty<OrderColumn>();
        }

        // An ordered index query selects each order column as "MAX(alias.[Column]) AS order_N" under its GROUP BY, and
        // orders by the alias.
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var select in new[] { Core, Statement })
        {
            foreach (var item in select.Items)
            {
                if (item.Alias is { } alias && IsOrderAlias(alias) && item.Expression is FunctionExpr { Arguments: [var argument] })
                {
                    if (argument is ColumnRef column)
                    {
                        aliases[alias] = ResolveColumn(column);
                    }
                    else if (IsRandom(argument))
                    {
                        aliases[alias] = RandomColumn;
                    }
                }
            }
        }

        var order = new List<OrderColumn>();
        foreach (var term in terms)
        {
            var column = term.Expression switch
            {
                AliasRefExpr alias => aliases.TryGetValue(alias.Name, out var mapped)
                    ? mapped
                    : throw Unsupported($"ORDER BY {alias.Name} is not a select item the provider can order by"),
                ColumnRef reference => ResolveColumn(reference),
                var other when IsRandom(other) => RandomColumn,
                _ => throw Unsupported("ORDER BY supports a column, a select item alias or the random order clause"),
            };
            order.Add(new OrderColumn(column, term.Descending));
        }

        return order;
    }
}
