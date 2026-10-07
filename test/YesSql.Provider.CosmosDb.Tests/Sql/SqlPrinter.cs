using System.Globalization;
using System.Text;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

/// <summary>
/// Writes a parsed statement back as SQL. The tests use it to check that parsing loses nothing: a statement and the
/// printed form of its parse tree must be the same once whitespace, case and bracket style are ignored.
/// </summary>
internal static class SqlPrinter
{
    public static string Print(SqlStatement statement)
    {
        var sb = new StringBuilder();
        Write(statement, sb);
        return sb.ToString();
    }

    public static string Print(SqlExpr expression)
    {
        var sb = new StringBuilder();
        Write(expression, sb);
        return sb.ToString();
    }

    private static void Write(SqlStatement statement, StringBuilder sb)
    {
        switch (statement)
        {
            case SelectStatement select:
                WriteSelect(select, sb);
                break;

            case InsertStatement insert:
                sb.Append("INSERT INTO [").Append(insert.Table).Append("] (");
                sb.Append(string.Join(", ", insert.Columns.Select(c => "[" + c + "]")));
                sb.Append(") VALUES (");
                WriteList(insert.Values, sb);
                sb.Append(')');
                if (insert.ReturningColumn is { } returning)
                {
                    sb.Append(" RETURNING [").Append(returning).Append(']');
                }

                break;

            case UpdateStatement update:
                sb.Append("UPDATE [").Append(update.Table).Append("] SET ");
                for (var i = 0; i < update.Assignments.Count; i++)
                {
                    sb.Append(i > 0 ? ", [" : "[").Append(update.Assignments[i].Column).Append("] = ");
                    Write(update.Assignments[i].Value, sb);
                }

                WriteWhere(update.Where, sb);
                break;

            case DeleteStatement delete:
                sb.Append("DELETE FROM [").Append(delete.Table).Append(']');
                WriteWhere(delete.Where, sb);
                break;

            case RenameColumnStatement rename:
                sb.Append("renamecolumn [").Append(rename.Table).Append("] [").Append(rename.From).Append("] [").Append(rename.To).Append(']');
                break;

            default:
                throw new InvalidOperationException(statement.GetType().Name);
        }
    }

    private static void WriteSelect(SelectStatement select, StringBuilder sb)
    {
        sb.Append("SELECT ");
        if (select.Distinct)
        {
            sb.Append("DISTINCT ");
        }

        for (var i = 0; i < select.Items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(select.Items[i].Expression, sb);
            if (select.Items[i].Alias is { } alias)
            {
                sb.Append(" AS ").Append(alias);
            }
        }

        if (select.From is { } from)
        {
            sb.Append(" FROM ");
            WriteSource(from, sb);
            foreach (var join in select.Joins)
            {
                sb.Append(join.Kind switch { JoinKind.Left => " LEFT JOIN ", JoinKind.Right => " RIGHT JOIN ", _ => " INNER JOIN " });
                WriteSource(join.Source, sb);
                sb.Append(" ON ");
                Write(join.On, sb);
            }
        }

        WriteWhere(select.Where, sb);

        if (select.GroupBy.Count > 0)
        {
            sb.Append(" GROUP BY ");
            WriteList(select.GroupBy, sb);
        }

        if (select.OrderBy.Count > 0)
        {
            sb.Append(" ORDER BY ");
            for (var i = 0; i < select.OrderBy.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                Write(select.OrderBy[i].Expression, sb);
                if (select.OrderBy[i].Descending)
                {
                    sb.Append(" DESC");
                }
            }
        }

        if (select.Offset is { } offset)
        {
            sb.Append(" OFFSET ").Append(offset.ToString(CultureInfo.InvariantCulture));
        }

        if (select.Limit is { } limit)
        {
            sb.Append(" LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WriteSource(SqlSource source, StringBuilder sb)
    {
        switch (source)
        {
            case NamedSource named:
                sb.Append('[').Append(named.Table).Append(']');
                break;

            case DerivedSource derived:
                sb.Append('(');
                WriteSelect(derived.Query, sb);
                sb.Append(')');
                break;
        }

        if (source.Alias is { } alias)
        {
            sb.Append(" AS ").Append(alias);
        }
    }

    private static void WriteWhere(SqlExpr? where, StringBuilder sb)
    {
        if (where is not null)
        {
            sb.Append(" WHERE ");
            Write(where, sb);
        }
    }

    private static void WriteList(IReadOnlyList<SqlExpr> items, StringBuilder sb)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(items[i], sb);
        }
    }

    private static void Write(SqlExpr expression, StringBuilder sb)
    {
        switch (expression)
        {
            case ColumnRef column:
                if (column.Qualifier is not null)
                {
                    sb.Append(column.Qualifier).Append('.');
                }

                sb.Append('[').Append(column.Name).Append(']');
                break;

            case ParamRef parameter:
                sb.Append('@').Append(parameter.Name);
                break;

            case LiteralExpr { Value: null }:
                sb.Append("NULL");
                break;

            case LiteralExpr { Value: string text }:
                sb.Append('\'').Append(text.Replace("'", "''")).Append('\'');
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
                Write(binary.Left, sb);
                sb.Append(' ').Append(binary.Operator).Append(' ');
                Write(binary.Right, sb);
                break;

            case IsNullExpr isNull:
                Write(isNull.Operand, sb);
                sb.Append(isNull.Negated ? " IS NOT NULL" : " IS NULL");
                break;

            case InListExpr list:
                Write(list.Operand, sb);
                sb.Append(list.Negated ? " NOT IN (" : " IN (");
                WriteList(list.Items, sb);
                sb.Append(')');
                break;

            case InSubqueryExpr subquery:
                Write(subquery.Operand, sb);
                sb.Append(subquery.Negated ? " NOT IN (SELECT " : " IN (SELECT ");
                Write(subquery.Query.Column, sb);
                sb.Append(" FROM [").Append(subquery.Query.Table).Append("] AS ").Append(subquery.Query.Alias);
                WriteWhere(subquery.Query.Where, sb);
                sb.Append(')');
                break;

            case FunctionExpr function:
                sb.Append(function.Name).Append('(');
                if (function.Distinct)
                {
                    sb.Append("DISTINCT ");
                }

                WriteList(function.Arguments, sb);
                sb.Append(')');
                break;

            case StarExpr star:
                sb.Append(star.Qualifier is null ? "*" : star.Qualifier + ".*");
                break;

            case QuotedNameExpr quoted:
                sb.Append('"').Append(quoted.Name).Append('"');
                break;

            case AliasRefExpr alias:
                sb.Append(alias.Name);
                break;

            default:
                throw new InvalidOperationException(expression.GetType().Name);
        }
    }
}
