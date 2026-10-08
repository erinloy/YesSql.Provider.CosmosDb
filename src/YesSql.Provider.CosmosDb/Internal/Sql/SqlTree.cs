using System;
using System.Collections.Generic;
using System.Linq;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>
/// The condition of a single-row update: the operand that gives the key, and the version the row has to have (null for an
/// update that is not checked), which a row with no version also meets when <see cref="AllowsNullVersion"/> is set.
/// </summary>
internal sealed record UpdateCondition(SqlExpr Key, long? Version, bool AllowsNullVersion);

/// <summary>Operations on a parsed expression that the translator needs, done on the tree and not on text.</summary>
internal static class SqlTree
{
    /// <summary>
    /// The top-level <c>AND</c> terms of a predicate, after removing parentheses that wrap the whole of it. A
    /// predicate that is not an <c>AND</c> is a single term.
    /// </summary>
    public static IReadOnlyList<SqlExpr> Conjuncts(SqlExpr? predicate)
    {
        if (predicate is null)
        {
            return Array.Empty<SqlExpr>();
        }

        while (predicate is ParenExpr paren)
        {
            predicate = paren.Inner;
        }

        return predicate is LogicalExpr { IsAnd: true } and ? and.Terms : new[] { predicate };
    }

    /// <summary>The inverse of <see cref="Conjuncts"/>: no term gives null, one term is itself, several are joined by <c>AND</c>.</summary>
    public static SqlExpr? And(IReadOnlyList<SqlExpr> terms)
        => terms.Count switch
        {
            0 => null,
            1 => terms[0],
            _ => new LogicalExpr(true, terms),
        };

    /// <summary>
    /// Removes the <c>[Document].[Type] = @p</c> terms YesSql adds to an index join. They are matched by shape: a
    /// column whose qualifier is a bracketed table name, called <c>Type</c>, compared with <c>=</c> to a parameter.
    /// Only terms joined by <c>AND</c> are removed, so an <c>OR</c> or <c>NOT</c> is never changed. Returns null when
    /// nothing is left.
    /// </summary>
    public static SqlExpr? WithoutDocumentTypePredicate(SqlExpr? predicate)
        => Without(predicate, term => term is BinaryExpr { Operator: "=", Left: ColumnRef { QualifierIsTable: true, Name: var name }, Right: ParamRef }
            && name.Equals("Type", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Removes the <c>[Type] = x</c> terms of a query over the document table, whatever the qualifier, where <c>x</c> is a
    /// parameter or a string literal. Only terms joined by <c>AND</c> are removed. What is left is every other condition
    /// of the query, or null when there is none.
    /// </summary>
    public static SqlExpr? WithoutTypeComparison(SqlExpr? predicate) => Without(predicate, IsTypeComparison);

    /// <summary>The value, a parameter or a string literal, in the first <c>[Type] = x</c> term under <c>AND</c>, or null when there is none.</summary>
    public static SqlExpr? TypeComparison(SqlExpr? predicate)
    {
        switch (predicate)
        {
            case BinaryExpr comparison when IsTypeComparison(comparison):
                return comparison.Right;
            case LogicalExpr { IsAnd: true } and:
                return and.Terms.Select(TypeComparison).FirstOrDefault(found => found is not null);
            case ParenExpr paren:
                return TypeComparison(paren.Inner);
            default:
                return null;
        }
    }

    private static bool IsTypeComparison(SqlExpr term)
        => term is BinaryExpr { Operator: "=", Left: ColumnRef column, Right: ParamRef or LiteralExpr { Value: string } }
           && column.Name.Equals("Type", StringComparison.OrdinalIgnoreCase);

    // The predicate without the terms that match, looking only at terms joined by AND.
    private static SqlExpr? Without(SqlExpr? predicate, Func<SqlExpr, bool> matches)
    {
        switch (predicate)
        {
            case null:
                return null;

            case LogicalExpr { IsAnd: true } and:
                var kept = and.Terms.Select(term => Without(term, matches)).Where(term => term is not null).Cast<SqlExpr>().ToList();
                return And(kept);

            case ParenExpr paren:
                var inner = Without(paren.Inner, matches);
                return inner is null ? null : new ParenExpr(inner);

            default:
                return matches(predicate) ? null : predicate;
        }
    }

    /// <summary>
    /// The name of the parameter in the <c>[Document].[Type] = @p</c> term that <see cref="WithoutDocumentTypePredicate"/>
    /// removes, or null when there is none. It is found the same way, by shape and only under <c>AND</c>.
    /// </summary>
    public static string? DocumentTypeParameter(SqlExpr? predicate)
    {
        switch (predicate)
        {
            case BinaryExpr { Operator: "=", Left: ColumnRef { QualifierIsTable: true, Name: var name }, Right: ParamRef parameter }
                when name.Equals("Type", StringComparison.OrdinalIgnoreCase):
                return parameter.Name;

            case LogicalExpr { IsAnd: true } and:
                return and.Terms.Select(DocumentTypeParameter).FirstOrDefault(found => found is not null);

            case ParenExpr paren:
                return DocumentTypeParameter(paren.Inner);

            default:
                return null;
        }
    }

    /// <summary>
    /// The operands of a predicate that selects documents by key and by nothing else, <c>[Id] = x</c> or
    /// <c>[Id] IN (x, ...)</c> where each <c>x</c> is a parameter or a whole number, or null for any other predicate.
    /// </summary>
    public static IReadOnlyList<SqlExpr>? KeyOperands(SqlExpr? predicate)
    {
        while (predicate is ParenExpr paren)
        {
            predicate = paren.Inner;
        }

        return predicate switch
        {
            BinaryExpr { Operator: "=", Left: ColumnRef column, Right: var key } when IsKey(column) && IsKeyOperand(key) => new[] { key },
            InListExpr { Negated: false, Operand: ColumnRef column } list when IsKey(column) && list.Items.All(IsKeyOperand) => list.Items,
            _ => null,
        };

        static bool IsKey(ColumnRef column) => column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase);
        static bool IsKeyOperand(SqlExpr operand) => operand is ParamRef or LiteralExpr { Value: long };
    }

    /// <summary>True when the predicate has an <c>[Id]</c> comparison anywhere in it, a sign that the query selects by key.</summary>
    public static bool MentionsId(SqlExpr? predicate)
    {
        var found = false;
        if (predicate is not null)
        {
            Visit(predicate, node =>
            {
                found |= node switch
                {
                    BinaryExpr { Operator: "=", Left: ColumnRef column } => column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase),
                    InListExpr { Negated: false, Operand: ColumnRef column } => column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase),
                    InSubqueryExpr { Negated: false, Operand: ColumnRef column } => column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
            });
        }

        return found;
    }

    /// <summary>
    /// The operand of each <c>[Column] = x</c> term of a predicate that is only such terms joined by <c>AND</c>, where <c>x</c> is a
    /// parameter or a literal, by column name. Returns null when the predicate has any other kind of term, or two terms for one column.
    /// </summary>
    public static IReadOnlyDictionary<string, SqlExpr>? EqualityOperands(SqlExpr? predicate)
    {
        var operands = new Dictionary<string, SqlExpr>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in Conjuncts(predicate))
        {
            if (term is BinaryExpr { Operator: "=", Left: ColumnRef column, Right: ParamRef or LiteralExpr } comparison
                && operands.TryAdd(column.Name, comparison.Right))
            {
                continue;
            }

            return null;
        }

        return operands;
    }

    /// <summary>
    /// The condition of a single-row <c>UPDATE</c>: <c>[Id] = x</c>, optionally with the version check YesSql adds to an update of a
    /// document, <c>[Version] = n</c> or <c>([Version] IS NULL OR [Version] = n)</c>, where <c>x</c> is a parameter or a whole number
    /// and <c>n</c> a whole number. Returns null for any other condition: a check that is not recognized must not be skipped.
    /// </summary>
    public static UpdateCondition? UpdateCondition(SqlExpr? predicate)
    {
        SqlExpr? key = null;
        long? version = null;
        var allowsNull = false;

        foreach (var term in Conjuncts(predicate))
        {
            var inner = term is ParenExpr paren ? paren.Inner : term;
            switch (inner)
            {
                case BinaryExpr { Operator: "=", Left: ColumnRef { Qualifier: null, Name: var name }, Right: var value }
                    when name.Equals("Id", StringComparison.OrdinalIgnoreCase) && key is null && value is ParamRef or LiteralExpr { Value: long }:
                    key = value;
                    break;

                case BinaryExpr { Operator: "=", Left: ColumnRef { Qualifier: null, Name: var name }, Right: LiteralExpr { Value: long number } }
                    when name.Equals("Version", StringComparison.OrdinalIgnoreCase) && version is null:
                    version = number;
                    break;

                case LogicalExpr { IsAnd: false, Terms: [var first, var second] } when version is null && IsNullOrVersion(first, second, out var number):
                    version = number;
                    allowsNull = true;
                    break;

                default:
                    return null;
            }
        }

        return key is null ? null : new UpdateCondition(key, version, allowsNull);
    }

    // [Version] IS NULL OR [Version] = n, in either order.
    private static bool IsNullOrVersion(SqlExpr first, SqlExpr second, out long version)
    {
        version = 0;
        if (second is IsNullExpr)
        {
            (first, second) = (second, first);
        }

        if (first is IsNullExpr { Negated: false, Operand: ColumnRef { Qualifier: null, Name: var nullName } }
            && second is BinaryExpr { Operator: "=", Left: ColumnRef { Qualifier: null, Name: var versionName }, Right: LiteralExpr { Value: long number } }
            && nullName.Equals("Version", StringComparison.OrdinalIgnoreCase)
            && versionName.Equals("Version", StringComparison.OrdinalIgnoreCase))
        {
            version = number;
            return true;
        }

        return false;
    }

    /// <summary>The distinct qualifiers of every column in the expression, including inside <c>IN (SELECT ...)</c>.</summary>
    public static IReadOnlySet<string> Qualifiers(SqlExpr expression) => Qualifiers(expression, enterSubqueries: true);

    private static IReadOnlySet<string> Qualifiers(SqlExpr expression, bool enterSubqueries)
    {
        var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(expression, node =>
        {
            if (node is ColumnRef { Qualifier: { } q })
            {
                qualifiers.Add(q);
            }
        }, enterSubqueries);
        return qualifiers;
    }

    /// <summary>
    /// Assigns each <c>AND</c> term of a predicate to the one index table it refers to, for a query that reads the joined index
    /// tables one at a time. <paramref name="tables"/> maps each join alias to its table. Aliases of one table, as when a query
    /// joins the same index twice, are one target, because the conditions are tested against the rows of that table. A term that
    /// refers to no join, to a column of the document, or to more than one table cannot be run against one table, and is refused
    /// instead of being dropped or applied to the wrong table. A term with no qualifier belongs to the only table when there is
    /// one. The result has an entry for every table.
    /// </summary>
    /// <exception cref="NotSupportedException">A term cannot be assigned to one table.</exception>
    public static IReadOnlyDictionary<string, List<SqlExpr>> SplitByTable(SqlExpr? predicate, IReadOnlyDictionary<string, string> tables, string sql)
    {
        var aliases = new Dictionary<string, string>(tables, StringComparer.OrdinalIgnoreCase);
        var split = tables.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(table => table, _ => new List<SqlExpr>(), StringComparer.OrdinalIgnoreCase);
        foreach (var term in Conjuncts(predicate))
        {
            // The columns inside an IN (SELECT ...) belong to the subquery, which is run on its own.
            var qualifiers = Qualifiers(term, enterSubqueries: false);
            var owners = qualifiers.Count == 0
                ? split.Keys.ToList()
                : qualifiers.Select(qualifier => aliases.TryGetValue(qualifier, out var table) ? table : null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (owners.Count != 1 || owners[0] is null)
            {
                var refers = qualifiers.Count == 0 ? "no table" : string.Join(", ", qualifiers);
                throw new NotSupportedException(
                    $"A condition that refers to {refers} cannot be run against one index table ({string.Join(", ", split.Keys)}): {sql}");
            }

            split[owners[0]!].Add(term);
        }

        return split;
    }

    /// <summary>The <c>IN (SELECT ...)</c> nodes of an expression that are not inside another subquery.</summary>
    public static IReadOnlyList<InSubqueryExpr> Subqueries(SqlExpr expression)
    {
        var found = new List<InSubqueryExpr>();
        Visit(expression, node =>
        {
            if (node is InSubqueryExpr subquery)
            {
                found.Add(subquery);
            }
        }, enterSubqueries: false);
        return found;
    }

    /// <summary>Calls <paramref name="action"/> for every node. The walk keeps its own stack, so tree depth cannot overflow it.</summary>
    public static void Visit(SqlExpr root, Action<SqlExpr> action, bool enterSubqueries = true)
    {
        var pending = new Stack<SqlExpr>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            action(node);

            switch (node)
            {
                case BinaryExpr binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;
                case LogicalExpr logical:
                    foreach (var term in logical.Terms.Reverse())
                    {
                        pending.Push(term);
                    }

                    break;
                case NotExpr not:
                    pending.Push(not.Operand);
                    break;
                case NegateExpr negate:
                    pending.Push(negate.Operand);
                    break;
                case IsNullExpr isNull:
                    pending.Push(isNull.Operand);
                    break;
                case ParenExpr paren:
                    pending.Push(paren.Inner);
                    break;
                case InListExpr list:
                    foreach (var item in list.Items.Reverse())
                    {
                        pending.Push(item);
                    }

                    pending.Push(list.Operand);
                    break;
                case InSubqueryExpr subquery:
                    pending.Push(subquery.Operand);
                    if (enterSubqueries)
                    {
                        if (subquery.Query.Where is { } where)
                        {
                            pending.Push(where);
                        }

                        pending.Push(subquery.Query.Column);
                    }

                    break;
                case FunctionExpr function:
                    foreach (var argument in function.Arguments.Reverse())
                    {
                        pending.Push(argument);
                    }

                    break;
            }
        }
    }
}
