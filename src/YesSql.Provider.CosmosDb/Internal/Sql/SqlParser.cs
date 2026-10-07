using System;
using System.Collections.Generic;
using System.Globalization;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>
/// Recursive-descent parser for the expression subset YesSql emits in <c>WHERE</c> clauses. Precedence, lowest to
/// highest: <c>OR</c>, <c>AND</c>, <c>NOT</c>, comparison (<c>= &lt;&gt; != &lt; &lt;= &gt; &gt;= LIKE IS IN</c>),
/// <c>+ - ||</c>, <c>* / %</c>, unary minus, primary. Anything else is rejected with a
/// <see cref="SqlSyntaxException"/> that names the position.
/// </summary>
internal sealed partial class SqlParser
{
    /// <summary>Nesting and operator-chain depth beyond which a statement is refused instead of risking the stack.</summary>
    internal const int MaxDepth = 200;

    private readonly string _sql;
    private readonly List<SqlToken> _tokens;
    private int _index;
    private int _depth;

    public SqlParser(string sql, List<SqlToken> tokens, int startIndex = 0)
    {
        _sql = sql;
        _tokens = tokens;
        _index = startIndex;
    }

    /// <summary>Index of the next unread token.</summary>
    public int Index => _index;

    /// <summary>Parses <paramref name="sql"/> as exactly one expression, optionally followed by a semicolon.</summary>
    public static SqlExpr ParseExpression(string sql)
    {
        var tokens = SqlLexer.Tokenize(sql);
        var parser = new SqlParser(sql, tokens);
        var expression = parser.ReadExpression();
        parser.SkipSemicolons();
        if (!parser.AtEnd)
        {
            throw parser.Error("Unexpected " + parser.Peek.Text + " after the expression");
        }

        return expression;
    }

    public bool AtEnd => _index >= _tokens.Count;

    public void SkipSemicolons()
    {
        while (!AtEnd && Peek.IsSymbol(";"))
        {
            _index++;
        }
    }

    /// <summary>Reads one expression at the current position.</summary>
    public SqlExpr ReadExpression() => ParseOr();

    private SqlToken Peek => _tokens[_index];

    private SqlSyntaxException Error(string message)
        => new(message, _sql, AtEnd ? _sql.Length : Peek.Position);

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            throw Error($"Expression is nested more than {MaxDepth} levels deep");
        }
    }

    private void Leave() => _depth--;

    private bool PeekWord(string word) => !AtEnd && Peek.IsWord(word);

    private bool PeekSymbol(string symbol) => !AtEnd && Peek.IsSymbol(symbol);

    private bool AcceptWord(string word)
    {
        if (PeekWord(word))
        {
            _index++;
            return true;
        }

        return false;
    }

    private bool AcceptSymbol(string symbol)
    {
        if (PeekSymbol(symbol))
        {
            _index++;
            return true;
        }

        return false;
    }

    private void ExpectSymbol(string symbol)
    {
        if (!AcceptSymbol(symbol))
        {
            throw Error($"Expected '{symbol}'" + (AtEnd ? " but the statement ended" : $" but found {Peek.Text}"));
        }
    }

    private void ExpectWord(string word)
    {
        if (!AcceptWord(word))
        {
            throw Error($"Expected {word.ToUpperInvariant()}" + (AtEnd ? " but the statement ended" : $" but found {Peek.Text}"));
        }
    }

    private SqlExpr ParseOr()
    {
        var first = ParseAnd();
        if (!PeekWord("or"))
        {
            return first;
        }

        var terms = new List<SqlExpr> { first };
        while (AcceptWord("or"))
        {
            terms.Add(ParseAnd());
        }

        return new LogicalExpr(false, terms);
    }

    private SqlExpr ParseAnd()
    {
        var first = ParseNot();
        if (!PeekWord("and"))
        {
            return first;
        }

        var terms = new List<SqlExpr> { first };
        while (AcceptWord("and"))
        {
            terms.Add(ParseNot());
        }

        return new LogicalExpr(true, terms);
    }

    private SqlExpr ParseNot()
    {
        if (AcceptWord("not"))
        {
            Enter();
            try
            {
                return new NotExpr(ParseNot());
            }
            finally
            {
                Leave();
            }
        }

        return ParseComparison();
    }

    private SqlExpr ParseComparison()
    {
        var left = ParseAdditive();

        if (!AtEnd)
        {
            var token = Peek;
            if (token.Kind == SqlTokenKind.Symbol && token.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
            {
                _index++;
                return new BinaryExpr(token.Text, left, ParseAdditive());
            }

            if (token.IsWord("is"))
            {
                _index++;
                var negated = AcceptWord("not");
                ExpectWord("null");
                return new IsNullExpr(left, negated);
            }

            if (token.IsWord("like"))
            {
                _index++;
                return new BinaryExpr("LIKE", left, ParseAdditive());
            }

            if (token.IsWord("in") || (token.IsWord("not") && _index + 1 < _tokens.Count && _tokens[_index + 1].IsWord("in")))
            {
                var negated = AcceptWord("not");
                ExpectWord("in");
                return ParseInOperand(left, negated);
            }

            if (token.IsWord("not") && _index + 1 < _tokens.Count && _tokens[_index + 1].IsWord("like"))
            {
                _index += 2;
                return new BinaryExpr("NOT LIKE", left, ParseAdditive());
            }
        }

        return left;
    }

    private SqlExpr ParseInOperand(SqlExpr operand, bool negated)
    {
        ExpectSymbol("(");

        if (PeekWord("select"))
        {
            var subquery = ParseSubSelect();
            ExpectSymbol(")");
            return new InSubqueryExpr(operand, subquery, negated);
        }

        var items = new List<SqlExpr>();
        do
        {
            items.Add(ParseAdditive());
        }
        while (AcceptSymbol(","));

        ExpectSymbol(")");
        return new InListExpr(operand, items, negated);
    }

    // SELECT [alias.]col FROM [table] AS alias [WHERE expr]
    private SubSelect ParseSubSelect()
    {
        ExpectWord("select");
        var column = ParseColumn();
        ExpectWord("from");
        if (AtEnd || Peek.Kind != SqlTokenKind.Bracketed)
        {
            throw Error("Expected a [table] after FROM");
        }

        var table = Peek.Value;
        _index++;
        ExpectWord("as");
        if (AtEnd || Peek.Kind != SqlTokenKind.Word)
        {
            throw Error("Expected a table alias after AS");
        }

        var alias = Peek.Text;
        _index++;

        SqlExpr? where = null;
        if (AcceptWord("where"))
        {
            Enter();
            try
            {
                where = ParseOr();
            }
            finally
            {
                Leave();
            }
        }

        return new SubSelect(column, table, alias, where);
    }

    private SqlExpr ParseAdditive()
    {
        var left = ParseMultiplicative();
        var chain = 0;
        while (!AtEnd && Peek.Kind == SqlTokenKind.Symbol && Peek.Text is "+" or "-" or "||")
        {
            if (++chain > MaxDepth)
            {
                throw Error($"More than {MaxDepth} chained operators");
            }

            var op = Peek.Text;
            _index++;
            left = new BinaryExpr(op, left, ParseMultiplicative());
        }

        return left;
    }

    private SqlExpr ParseMultiplicative()
    {
        var left = ParseUnary();
        var chain = 0;
        while (!AtEnd && Peek.Kind == SqlTokenKind.Symbol && Peek.Text is "*" or "/" or "%")
        {
            if (++chain > MaxDepth)
            {
                throw Error($"More than {MaxDepth} chained operators");
            }

            var op = Peek.Text;
            _index++;
            left = new BinaryExpr(op, left, ParseUnary());
        }

        return left;
    }

    private SqlExpr ParseUnary()
    {
        if (AcceptSymbol("-"))
        {
            Enter();
            try
            {
                return new NegateExpr(ParseUnary());
            }
            finally
            {
                Leave();
            }
        }

        return ParsePrimary();
    }

    private SqlExpr ParsePrimary()
    {
        if (AtEnd)
        {
            throw Error("Expected an expression but the statement ended");
        }

        var token = Peek;
        switch (token.Kind)
        {
            case SqlTokenKind.Number:
                _index++;
                return new LiteralExpr(ParseNumber(token));

            case SqlTokenKind.String:
                _index++;
                return new LiteralExpr(token.Value);

            case SqlTokenKind.Quoted:
                _index++;
                return new QuotedNameExpr(token.Value);

            case SqlTokenKind.Parameter:
                _index++;
                return new ParamRef(token.Value);

            case SqlTokenKind.Bracketed:
                return ParseColumn();

            case SqlTokenKind.Symbol when token.Text == "(":
                _index++;
                Enter();
                try
                {
                    var inner = ParseOr();
                    ExpectSymbol(")");
                    return new ParenExpr(inner);
                }
                finally
                {
                    Leave();
                }

            case SqlTokenKind.Word:
                return ParseWordPrimary(token);

            default:
                throw Error($"Unexpected {token.Text}");
        }
    }

    // NULL, TRUE, FALSE, a function call, or alias.[Column].
    private SqlExpr ParseWordPrimary(SqlToken token)
    {
        if (token.IsWord("null"))
        {
            _index++;
            return new LiteralExpr(null);
        }

        if (token.IsWord("true") || token.IsWord("false"))
        {
            _index++;
            return new LiteralExpr(token.IsWord("true"));
        }

        var next = _index + 1 < _tokens.Count ? _tokens[_index + 1] : (SqlToken?)null;
        if (next is { } n && n.IsSymbol("("))
        {
            _index += 2;
            Enter();
            try
            {
                var args = new List<SqlExpr>();
                var distinct = AcceptWord("distinct");
                if (!PeekSymbol(")"))
                {
                    do
                    {
                        args.Add(PeekSymbol("*") && !distinct ? ReadStar() : ParseOr());
                    }
                    while (AcceptSymbol(","));
                }

                ExpectSymbol(")");
                return new FunctionExpr(token.Text, args, distinct);
            }
            finally
            {
                Leave();
            }
        }

        if (next is { } d && d.IsSymbol("."))
        {
            return ParseColumn();
        }

        throw Error($"Unexpected word {token.Text}");
    }

    private ColumnRef ParseColumn()
    {
        string? qualifier = null;
        var qualifierIsTable = false;
        var token = AtEnd ? throw Error("Expected a column but the statement ended") : Peek;

        if (token.Kind is SqlTokenKind.Word or SqlTokenKind.Bracketed
            && _index + 1 < _tokens.Count && _tokens[_index + 1].IsSymbol("."))
        {
            qualifierIsTable = token.Kind == SqlTokenKind.Bracketed;
            qualifier = qualifierIsTable ? token.Value : token.Text;
            _index += 2;
            token = AtEnd ? throw Error("Expected a column name after '.'") : Peek;
        }

        if (token.Kind != SqlTokenKind.Bracketed)
        {
            throw Error("Expected a [column]" + $" but found {token.Text}");
        }

        _index++;
        return new ColumnRef(qualifier, token.Value, qualifierIsTable);
    }

    private object ParseNumber(SqlToken token)
    {
        if (long.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        if (decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw Error($"Number {token.Text} is out of range");
    }
}
