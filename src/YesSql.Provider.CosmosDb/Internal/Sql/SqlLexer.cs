using System;
using System.Collections.Generic;
using System.Text;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>
/// Splits a statement into tokens. It reads one character at a time, never backtracks and never allocates more than
/// the tokens it returns, so its cost is linear in the length of the statement. Input outside the supported alphabet
/// is rejected with <see cref="SqlSyntaxException"/>.
/// </summary>
internal static class SqlLexer
{
    public static List<SqlToken> Tokenize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '[')
            {
                tokens.Add(ReadBracketed(sql, ref i));
            }
            else if (c == '@')
            {
                tokens.Add(ReadParameter(sql, ref i));
            }
            else if (c == '\'')
            {
                tokens.Add(ReadQuoted(sql, ref i, '\'', SqlTokenKind.String));
            }
            else if (c == '"')
            {
                tokens.Add(ReadQuoted(sql, ref i, '"', SqlTokenKind.Quoted));
            }
            else if (IsDigit(c))
            {
                tokens.Add(ReadNumber(sql, ref i));
            }
            else if (IsWordStart(c))
            {
                var start = i;
                while (i < sql.Length && IsWordPart(sql[i]))
                {
                    i++;
                }

                var text = sql.Substring(start, i - start);
                tokens.Add(new SqlToken(SqlTokenKind.Word, text, text, start));
            }
            else
            {
                tokens.Add(ReadSymbol(sql, ref i));
            }
        }

        return tokens;
    }

    private static SqlToken ReadBracketed(string sql, ref int i)
    {
        var start = i;
        var name = new StringBuilder();
        i++; // [
        while (true)
        {
            if (i >= sql.Length)
            {
                throw new SqlSyntaxException("Unterminated [identifier]", sql, start);
            }

            if (sql[i] == ']')
            {
                if (i + 1 < sql.Length && sql[i + 1] == ']')
                {
                    name.Append(']'); // ]] escapes a closing bracket
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            name.Append(sql[i]);
            i++;
        }

        if (name.Length == 0)
        {
            throw new SqlSyntaxException("Empty [identifier]", sql, start);
        }

        return new SqlToken(SqlTokenKind.Bracketed, sql.Substring(start, i - start), name.ToString(), start);
    }

    private static SqlToken ReadParameter(string sql, ref int i)
    {
        var start = i;
        i++; // @
        var nameStart = i;
        while (i < sql.Length && IsWordPart(sql[i]))
        {
            i++;
        }

        if (i == nameStart)
        {
            throw new SqlSyntaxException("Expected a parameter name after '@'", sql, start);
        }

        return new SqlToken(SqlTokenKind.Parameter, sql.Substring(start, i - start), sql.Substring(nameStart, i - nameStart), start);
    }

    // The delimiter is escaped by doubling it. A backslash is an ordinary character, as in standard SQL.
    private static SqlToken ReadQuoted(string sql, ref int i, char quote, SqlTokenKind kind)
    {
        var start = i;
        var value = new StringBuilder();
        i++; // opening quote
        while (true)
        {
            if (i >= sql.Length)
            {
                throw new SqlSyntaxException($"Unterminated {quote}-quoted literal", sql, start);
            }

            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    value.Append(quote);
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            value.Append(sql[i]);
            i++;
        }

        return new SqlToken(kind, sql.Substring(start, i - start), value.ToString(), start);
    }

    private static SqlToken ReadNumber(string sql, ref int i)
    {
        var start = i;
        while (i < sql.Length && IsDigit(sql[i]))
        {
            i++;
        }

        if (i + 1 < sql.Length && sql[i] == '.' && IsDigit(sql[i + 1]))
        {
            i++;
            while (i < sql.Length && IsDigit(sql[i]))
            {
                i++;
            }
        }

        // A number run straight into a word (12abc) is not valid; refuse it rather than split it silently.
        if (i < sql.Length && IsWordStart(sql[i]))
        {
            throw new SqlSyntaxException("A number cannot be followed directly by a letter", sql, start);
        }

        var text = sql.Substring(start, i - start);
        return new SqlToken(SqlTokenKind.Number, text, text, start);
    }

    private static SqlToken ReadSymbol(string sql, ref int i)
    {
        var start = i;
        var c = sql[i];

        if (i + 1 < sql.Length)
        {
            var two = (c, sql[i + 1]);
            if (two is ('<', '>') or ('<', '=') or ('>', '=') or ('!', '=') or ('|', '|'))
            {
                i += 2;
                var pair = sql.Substring(start, 2);
                return new SqlToken(SqlTokenKind.Symbol, pair, pair, start);
            }
        }

        if (c is '(' or ')' or ',' or '.' or ';' or '=' or '<' or '>' or '*' or '+' or '-' or '/' or '%')
        {
            i++;
            var one = c.ToString();
            return new SqlToken(SqlTokenKind.Symbol, one, one, start);
        }

        throw new SqlSyntaxException($"Unexpected character '{c}'", sql, start);
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsWordStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsWordPart(char c) => IsWordStart(c) || IsDigit(c);
}
