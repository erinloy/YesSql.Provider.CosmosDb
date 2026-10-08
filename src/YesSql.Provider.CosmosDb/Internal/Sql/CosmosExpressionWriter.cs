using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>
/// Writes a parsed expression as Cosmos DB query text. Column names come from the parse tree and are written as
/// <c>c["Name"]</c>, string literals are handed to a binder callback to become query parameters, and
/// <c>IN (SELECT ...)</c> is handed to a resolver callback, so nothing the statement contained is
/// written into the query as text except numbers and operators.
/// </summary>
internal sealed class CosmosExpressionWriter
{
    private static readonly HashSet<string> ComparisonOperators = new(StringComparer.Ordinal) { "=", "!=", "<>", "<", "<=", ">", ">=" };

    private readonly Func<InSubqueryExpr, string> _resolveSubquery;
    private readonly Func<string, string> _bindLiteral;
    private readonly ISet<string> _dateParameters;

    /// <param name="resolveSubquery">Runs a subquery and returns the name of the query parameter that holds its values.</param>
    /// <param name="bindLiteral">Returns the name of a query parameter that holds a string literal.</param>
    /// <param name="dateParameters">Names (without <c>@</c>) of parameters that hold dates, which are compared by instant.</param>
    public CosmosExpressionWriter(
        Func<InSubqueryExpr, string> resolveSubquery,
        Func<string, string> bindLiteral,
        ISet<string>? dateParameters = null)
    {
        _resolveSubquery = resolveSubquery;
        _bindLiteral = bindLiteral;
        _dateParameters = dateParameters ?? new HashSet<string>();
    }

    public string Write(SqlExpr expression)
    {
        var builder = new StringBuilder();
        Write(expression, builder);
        return builder.ToString();
    }

    /// <summary>The Cosmos text for a column reference.</summary>
    public static string Column(ColumnRef column) => Property(column.Name);

    /// <summary>The Cosmos text for a property of the item, <c>c["Name"]</c>.</summary>
    public static string Property(string name) => "c[\"" + EscapeName(name) + "\"]";

    /// <summary>A name written as a double quoted string, such as a table name compared with the <c>__table</c> field.</summary>
    public static string StringLiteral(string name) => "\"" + EscapeName(name) + "\"";

    private static string EscapeName(string name)
    {
        // Property names are written between double quotes, so a name that could end the string is refused.
        foreach (var c in name)
        {
            if (c is '"' or '\\' or < ' ')
            {
                throw new SqlSyntaxException($"Column name {name} contains a character that is not supported.");
            }
        }

        return name;
    }

    private void Write(SqlExpr expression, StringBuilder sb)
    {
        switch (expression)
        {
            case ColumnRef column:
                sb.Append(Column(column));
                break;

            case ParamRef parameter:
                sb.Append('@').Append(parameter.Name);
                break;

            case LiteralExpr { Value: null }:
                sb.Append("null");
                break;

            case LiteralExpr { Value: bool flag }:
                sb.Append(flag ? "true" : "false");
                break;

            case LiteralExpr { Value: string text }:
                sb.Append(_bindLiteral(text));
                break;

            case LiteralExpr { Value: IFormattable number }:
                sb.Append(number.ToString(null, CultureInfo.InvariantCulture));
                break;

            case ParenExpr paren:
                sb.Append('(');
                Write(paren.Inner, sb);
                sb.Append(')');
                break;

            case LogicalExpr logical:
                for (var i = 0; i < logical.Terms.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(logical.IsAnd ? " AND " : " OR ");
                    }

                    Write(logical.Terms[i], sb);
                }

                break;

            case NotExpr not:
                sb.Append("NOT ");
                Write(not.Operand, sb);
                break;

            case NegateExpr negate:
                sb.Append('-');
                Write(negate.Operand, sb);
                break;

            case BinaryExpr binary:
                WriteBinary(binary, sb);
                break;

            case IsNullExpr isNull:
                WriteIsNull(isNull, sb);
                break;

            case InListExpr list:
                Write(list.Operand, sb);
                sb.Append(list.Negated ? " NOT IN (" : " IN (");
                for (var i = 0; i < list.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(list.Items[i], sb);
                }

                sb.Append(')');
                break;

            case InSubqueryExpr subquery:
                sb.Append(subquery.Negated ? "NOT ARRAY_CONTAINS(" : "ARRAY_CONTAINS(");
                sb.Append(_resolveSubquery(subquery)).Append(", ");
                Write(subquery.Operand, sb);
                sb.Append(')');
                break;

            case FunctionExpr function:
                sb.Append(function.Name).Append('(');
                for (var i = 0; i < function.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(function.Arguments[i], sb);
                }

                sb.Append(')');
                break;

            default:
                throw new SqlSyntaxException($"Expression {expression.GetType().Name} is not supported.");
        }
    }

    private void WriteBinary(BinaryExpr binary, StringBuilder sb)
    {
        // A date column compared with a date parameter is compared by instant, so a DateTimeOffset stored with an
        // offset ("...+00:00") matches a DateTime value ("...Z") for the same moment.
        if (ComparisonOperators.Contains(binary.Operator) && IsDateComparison(binary.Left, binary.Right))
        {
            sb.Append("DateTimeToTimestamp(");
            Write(binary.Left, sb);
            sb.Append(") ").Append(binary.Operator).Append(" DateTimeToTimestamp(");
            Write(binary.Right, sb);
            sb.Append(')');
            return;
        }

        Write(binary.Left, sb);
        sb.Append(' ').Append(binary.Operator).Append(' ');
        Write(binary.Right, sb);
    }

    private bool IsDateComparison(SqlExpr left, SqlExpr right)
        => (left is ColumnRef && right is ParamRef rp && _dateParameters.Contains(rp.Name))
           || (left is ParamRef lp && _dateParameters.Contains(lp.Name) && right is ColumnRef);

    private void WriteIsNull(IsNullExpr isNull, StringBuilder sb)
    {
        if (isNull.Operand is not ColumnRef column)
        {
            throw new SqlSyntaxException("IS [NOT] NULL is only supported on a column.");
        }

        var text = Column(column);
        sb.Append(isNull.Negated
            ? $"(IS_DEFINED({text}) AND NOT IS_NULL({text}))"
            : $"(NOT IS_DEFINED({text}) OR IS_NULL({text}))");
    }
}
