using System;
using System.Collections.Generic;
using System.Linq;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

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
    {
        switch (predicate)
        {
            case null:
                return null;

            case BinaryExpr { Operator: "=", Left: ColumnRef { QualifierIsTable: true, Name: var name }, Right: ParamRef }
                when name.Equals("Type", StringComparison.OrdinalIgnoreCase):
                return null;

            case LogicalExpr { IsAnd: true } and:
                var kept = and.Terms.Select(WithoutDocumentTypePredicate).Where(t => t is not null).Cast<SqlExpr>().ToList();
                return And(kept);

            case ParenExpr paren:
                var inner = WithoutDocumentTypePredicate(paren.Inner);
                return inner is null ? null : new ParenExpr(inner);

            default:
                return predicate;
        }
    }

    /// <summary>The distinct qualifiers of every column in the expression, including inside <c>IN (SELECT ...)</c>.</summary>
    public static IReadOnlySet<string> Qualifiers(SqlExpr expression)
    {
        var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(expression, node =>
        {
            if (node is ColumnRef { Qualifier: { } q })
            {
                qualifiers.Add(q);
            }
        });
        return qualifiers;
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
