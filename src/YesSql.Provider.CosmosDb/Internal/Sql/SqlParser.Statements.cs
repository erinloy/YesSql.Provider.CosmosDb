using System;
using System.Collections.Generic;
using System.Globalization;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

// Statement-level grammar. Expressions are read by the methods in SqlParser.cs with the same cursor.
internal sealed partial class SqlParser
{
    /// <summary>
    /// Parses one statement: <c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or the
    /// <c>renamecolumn</c> schema command, optionally followed by a semicolon.
    /// </summary>
    public static SqlStatement ParseStatement(string sql)
    {
        var tokens = SqlLexer.Tokenize(sql);
        var parser = new SqlParser(sql, tokens);
        var statement = parser.ReadStatement();
        parser.SkipSemicolons();
        if (!parser.AtEnd)
        {
            throw parser.Error("Unexpected " + parser.Peek.Text + " after the statement");
        }

        return statement;
    }

    private SqlStatement ReadStatement()
    {
        if (AtEnd)
        {
            throw Error("The statement is empty");
        }

        if (PeekWord("select"))
        {
            return ReadSelect();
        }

        if (PeekWord("insert"))
        {
            return ReadInsert();
        }

        if (PeekWord("update"))
        {
            return ReadUpdate();
        }

        if (PeekWord("delete"))
        {
            return ReadDelete();
        }

        if (PeekWord("renamecolumn"))
        {
            return ReadRenameColumn();
        }

        throw Error($"Unsupported statement starting with {Peek.Text}; expected SELECT, INSERT, UPDATE, DELETE or renamecolumn");
    }

    private StarExpr ReadStar()
    {
        ExpectSymbol("*");
        return new StarExpr(null);
    }

    private SelectStatement ReadSelect()
    {
        Enter();
        try
        {
            ExpectWord("select");
            var distinct = AcceptWord("distinct");

            var items = new List<SelectItem>();
            do
            {
                items.Add(ReadSelectItem());
            }
            while (AcceptSymbol(","));

            SqlSource? from = null;
            var joins = new List<JoinClause>();
            if (AcceptWord("from"))
            {
                from = ReadSource();
                while (PeekWord("inner") || PeekWord("left") || PeekWord("right") || PeekWord("join"))
                {
                    var kind = JoinKind.Inner;
                    if (AcceptWord("left"))
                    {
                        kind = JoinKind.Left;
                        AcceptWord("outer");
                    }
                    else if (AcceptWord("right"))
                    {
                        kind = JoinKind.Right;
                        AcceptWord("outer");
                    }
                    else
                    {
                        AcceptWord("inner");
                    }

                    ExpectWord("join");
                    var source = ReadSource();
                    ExpectWord("on");
                    joins.Add(new JoinClause(source, ParseOr(), kind));
                }
            }

            SqlExpr? where = null;
            if (AcceptWord("where"))
            {
                where = ParseOr();
            }

            var groupBy = new List<SqlExpr>();
            if (AcceptWord("group"))
            {
                ExpectWord("by");
                do
                {
                    groupBy.Add(ParseOr());
                }
                while (AcceptSymbol(","));
            }

            var orderBy = new List<OrderTerm>();
            if (AcceptWord("order"))
            {
                ExpectWord("by");
                do
                {
                    orderBy.Add(ReadOrderTerm());
                }
                while (AcceptSymbol(","));
            }

            long? offset = null;
            long? limit = null;
            while (true)
            {
                if (offset is null && AcceptWord("offset"))
                {
                    offset = ReadInteger("OFFSET");
                    AcceptWord("rows");
                }
                else if (limit is null && AcceptWord("limit"))
                {
                    limit = ReadInteger("LIMIT");
                }
                else
                {
                    break;
                }
            }

            return new SelectStatement(distinct, items, from, joins, where, groupBy, orderBy, offset, limit);
        }
        finally
        {
            Leave();
        }
    }

    private SelectItem ReadSelectItem()
    {
        if (PeekSymbol("*"))
        {
            _index++;
            return new SelectItem(new StarExpr(null), null);
        }

        // alias.* or [Table].*
        if (!AtEnd && Peek.Kind is SqlTokenKind.Word or SqlTokenKind.Bracketed
            && _index + 2 < _tokens.Count && _tokens[_index + 1].IsSymbol(".") && _tokens[_index + 2].IsSymbol("*"))
        {
            var qualifier = Peek.Kind == SqlTokenKind.Bracketed ? Peek.Value : Peek.Text;
            _index += 3;
            return new SelectItem(new StarExpr(qualifier), null);
        }

        var expression = ParseOr();
        string? alias = null;
        if (AcceptWord("as"))
        {
            alias = ReadAlias();
        }

        return new SelectItem(expression, alias);
    }

    private string ReadAlias()
    {
        if (AtEnd || Peek.Kind is not (SqlTokenKind.Word or SqlTokenKind.Bracketed))
        {
            throw Error("Expected an alias" + (AtEnd ? " but the statement ended" : $" but found {Peek.Text}"));
        }

        var alias = Peek.Kind == SqlTokenKind.Bracketed ? Peek.Value : Peek.Text;
        _index++;
        return alias;
    }

    private SqlSource ReadSource()
    {
        if (AcceptSymbol("("))
        {
            if (!PeekWord("select"))
            {
                throw Error("Expected SELECT in the derived table");
            }

            var query = ReadSelect();
            ExpectSymbol(")");
            ExpectWord("as");
            return new DerivedSource(query, ReadAlias());
        }

        if (AtEnd || Peek.Kind != SqlTokenKind.Bracketed)
        {
            throw Error("Expected a [table]" + (AtEnd ? " but the statement ended" : $" but found {Peek.Text}"));
        }

        var table = Peek.Value;
        _index++;
        return new NamedSource(table, AcceptWord("as") ? ReadAlias() : null);
    }

    // ORDER BY accepts an expression or a bare word that names a select item alias (ORDER BY order_1).
    private OrderTerm ReadOrderTerm()
    {
        SqlExpr expression;
        var isBareWord = !AtEnd && Peek.Kind == SqlTokenKind.Word
            && !(_index + 1 < _tokens.Count && (_tokens[_index + 1].IsSymbol("(") || _tokens[_index + 1].IsSymbol(".")))
            && !Peek.IsWord("null") && !Peek.IsWord("not");
        if (isBareWord)
        {
            expression = new AliasRefExpr(Peek.Text);
            _index++;
        }
        else
        {
            expression = ParseOr();
        }

        var descending = false;
        if (AcceptWord("desc"))
        {
            descending = true;
        }
        else
        {
            AcceptWord("asc");
        }

        return new OrderTerm(expression, descending);
    }

    private long ReadInteger(string clause)
    {
        if (AtEnd || Peek.Kind != SqlTokenKind.Number
            || !long.TryParse(Peek.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw Error($"{clause} needs a whole number" + (AtEnd ? " but the statement ended" : $" but found {Peek.Text}"));
        }

        _index++;
        return value;
    }

    private string ReadBracketedName(string what)
    {
        if (AtEnd || Peek.Kind != SqlTokenKind.Bracketed)
        {
            throw Error($"Expected a [{what}]" + (AtEnd ? " but the statement ended" : $" but found {Peek.Text}"));
        }

        var name = Peek.Value;
        _index++;
        return name;
    }

    private InsertStatement ReadInsert()
    {
        ExpectWord("insert");
        ExpectWord("into");
        var table = ReadBracketedName("table");

        ExpectSymbol("(");
        var columns = new List<string>();
        do
        {
            columns.Add(ReadBracketedName("column"));
        }
        while (AcceptSymbol(","));
        ExpectSymbol(")");

        ExpectWord("values");
        ExpectSymbol("(");
        var values = new List<SqlExpr>();
        do
        {
            values.Add(ParseOr());
        }
        while (AcceptSymbol(","));
        ExpectSymbol(")");

        if (columns.Count != values.Count)
        {
            throw Error($"INSERT names {columns.Count} columns but supplies {values.Count} values");
        }

        var returning = AcceptWord("returning") ? ReadBracketedName("column") : null;
        return new InsertStatement(table, columns, values, returning);
    }

    private UpdateStatement ReadUpdate()
    {
        ExpectWord("update");
        var table = ReadBracketedName("table");
        ExpectWord("set");

        var assignments = new List<Assignment>();
        do
        {
            var column = ReadBracketedName("column");
            ExpectSymbol("=");
            assignments.Add(new Assignment(column, ParseOr()));
        }
        while (AcceptSymbol(","));

        SqlExpr? where = AcceptWord("where") ? ParseOr() : null;
        return new UpdateStatement(table, assignments, where);
    }

    private DeleteStatement ReadDelete()
    {
        ExpectWord("delete");
        ExpectWord("from");
        var table = ReadBracketedName("table");
        SqlExpr? where = AcceptWord("where") ? ParseOr() : null;
        return new DeleteStatement(table, where);
    }

    private RenameColumnStatement ReadRenameColumn()
    {
        ExpectWord("renamecolumn");
        var table = ReadBracketedName("table");
        var from = ReadBracketedName("current column name");
        var to = ReadBracketedName("new column name");
        return new RenameColumnStatement(table, from, to);
    }
}
