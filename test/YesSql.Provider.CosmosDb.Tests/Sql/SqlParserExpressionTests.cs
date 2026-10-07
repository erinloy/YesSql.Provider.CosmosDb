using System.Diagnostics;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

public class SqlParserExpressionTests
{
    private static SqlExpr Parse(string sql) => SqlParser.ParseExpression(sql);

    [Fact]
    public void And_binds_tighter_than_or_and_chains_are_flat()
    {
        var tree = Assert.IsType<LogicalExpr>(Parse("[a] = 1 OR [b] = 2 AND [c] = 3 AND [d] = 4"));

        Assert.False(tree.IsAnd);
        Assert.Equal(2, tree.Terms.Count);
        var and = Assert.IsType<LogicalExpr>(tree.Terms[1]);
        Assert.True(and.IsAnd);
        Assert.Equal(3, and.Terms.Count);
    }

    [Fact]
    public void Explicit_parentheses_are_kept_in_the_tree()
    {
        var tree = Assert.IsType<LogicalExpr>(Parse("([a] = 1 OR [b] = 2) AND [c] = 3"));

        Assert.True(tree.IsAnd);
        var paren = Assert.IsType<ParenExpr>(tree.Terms[0]);
        Assert.False(Assert.IsType<LogicalExpr>(paren.Inner).IsAnd);
    }

    [Fact]
    public void Columns_carry_their_qualifier_and_parameters_drop_the_at_sign()
    {
        var tree = Assert.IsType<BinaryExpr>(Parse("PersonByName_a1.[SomeName] = @p0"));

        Assert.Equal(new ColumnRef("PersonByName_a1", "SomeName"), tree.Left);
        Assert.Equal(new ParamRef("p0"), tree.Right);
        Assert.Equal(new ColumnRef("Document", "Id"), Assert.IsType<BinaryExpr>(Parse("[Document].[Id] = @p")).Left);
        Assert.Equal(new ColumnRef(null, "Id"), Assert.IsType<BinaryExpr>(Parse("[Id] = @p")).Left);
    }

    [Theory]
    [InlineData("[a] <> @p", "<>")]
    [InlineData("[a] != @p", "!=")]
    [InlineData("[a] <= @p", "<=")]
    [InlineData("[a] >= @p", ">=")]
    [InlineData("[a] < @p", "<")]
    [InlineData("[a] > @p", ">")]
    [InlineData("[a] like @p", "LIKE")]
    public void Comparison_operators_are_recognized(string sql, string expected)
        => Assert.Equal(expected, Assert.IsType<BinaryExpr>(Parse(sql)).Operator);

    [Fact]
    public void Null_tests_in_lists_and_in_subqueries_parse()
    {
        Assert.False(Assert.IsType<IsNullExpr>(Parse("[a] IS NULL")).Negated);
        Assert.True(Assert.IsType<IsNullExpr>(Parse("[a] IS NOT NULL")).Negated);

        var list = Assert.IsType<InListExpr>(Parse("[a] IN (@p0, @p1, 3, 'x')"));
        Assert.False(list.Negated);
        Assert.Equal(4, list.Items.Count);
        Assert.True(Assert.IsType<InListExpr>(Parse("[a] NOT IN (@p0)")).Negated);

        var sub = Assert.IsType<InSubqueryExpr>(Parse(
            "x_a1.[Nick] IN (SELECT y_a1.[Name] FROM [tpPersonByName] AS y_a1 WHERE (y_a1.[Name] like @p0))"));
        Assert.Equal("tpPersonByName", sub.Query.Table);
        Assert.Equal("y_a1", sub.Query.Alias);
        Assert.Equal(new ColumnRef("y_a1", "Name"), sub.Query.Column);
        Assert.IsType<ParenExpr>(sub.Query.Where);

        var withoutWhere = Assert.IsType<InSubqueryExpr>(Parse("[a] NOT IN (SELECT t.[b] FROM [T] AS t)"));
        Assert.True(withoutWhere.Negated);
        Assert.Null(withoutWhere.Query.Where);
    }

    [Fact]
    public void Not_not_like_arithmetic_concatenation_and_functions_parse()
    {
        Assert.IsType<NotExpr>(Parse("NOT [a] = @p"));
        Assert.Equal("NOT LIKE", Assert.IsType<BinaryExpr>(Parse("[a] NOT LIKE @p")).Operator);

        // * binds tighter than +, and || chains with +
        var sum = Assert.IsType<BinaryExpr>(Parse("[a] + [b] * 2"));
        Assert.Equal("+", sum.Operator);
        Assert.Equal("*", Assert.IsType<BinaryExpr>(sum.Right).Operator);

        var concat = Assert.IsType<BinaryExpr>(Parse("(([a] || @p) || [b]) = @q"));
        Assert.Equal("=", concat.Operator);

        Assert.IsType<NegateExpr>(Parse("-[a]"));

        var function = Assert.IsType<FunctionExpr>(Parse("MAX([a])"));
        Assert.Equal("MAX", function.Name);
        Assert.Single(function.Arguments);
        Assert.Empty(Assert.IsType<FunctionExpr>(Parse("GetCurrentTimestamp()")).Arguments);
    }

    [Fact]
    public void Literals_have_typed_values()
    {
        Assert.Equal(42L, Assert.IsType<LiteralExpr>(Assert.IsType<BinaryExpr>(Parse("[a] = 42")).Right).Value);
        Assert.Equal(1.5m, Assert.IsType<LiteralExpr>(Assert.IsType<BinaryExpr>(Parse("[a] = 1.5")).Right).Value);
        Assert.Equal("it's", Assert.IsType<LiteralExpr>(Assert.IsType<BinaryExpr>(Parse("[a] = 'it''s'")).Right).Value);
        Assert.Null(Assert.IsType<LiteralExpr>(Assert.IsType<BinaryExpr>(Parse("[a] = NULL")).Right).Value);
    }

    [Fact]
    public void A_trailing_semicolon_is_allowed()
        => Assert.IsType<BinaryExpr>(Parse("[a] = @p ;"));

    [Theory]
    [InlineData("[a] = @p extra", "Unexpected extra")]
    [InlineData("([a] = @p", "Expected ')'")]
    [InlineData("[a] =", "statement ended")]
    [InlineData("", "statement ended")]
    [InlineData("foo = 1", "Unexpected word foo")]
    [InlineData("[a] IN ()", "Unexpected )")]
    [InlineData("[a] IS 5", "Expected NULL")]
    [InlineData("[a] IN (SELECT [b] FROM)", "Expected a [table]")]
    [InlineData("[a] = @p AND", "statement ended")]
    [InlineData("[a] == @p", "Unexpected =")]
    [InlineData("[a] = @p)", "Unexpected )")]
    public void Malformed_expressions_are_rejected_with_a_position(string sql, string expected)
    {
        var ex = Assert.Throws<SqlSyntaxException>(() => Parse(sql));
        Assert.Contains(expected, ex.Message);
        Assert.InRange(ex.Position, 0, sql.Length);
    }

    [Fact]
    public void A_long_flat_chain_is_fine_but_a_deep_nest_is_refused_without_overflowing_the_stack()
    {
        var chain = string.Join(" OR ", Enumerable.Range(0, 50_000).Select(i => $"[a] = @p{i}"));
        Assert.Equal(50_000, Assert.IsType<LogicalExpr>(Parse(chain)).Terms.Count);

        foreach (var nest in new[]
        {
            new string('(', 100_000) + "[a] = 1" + new string(')', 100_000),
            string.Concat(Enumerable.Repeat("NOT ", 100_000)) + "[a] = 1",
            string.Concat(Enumerable.Repeat("-", 100_000)) + "1",
            "[a] = " + string.Join(" + ", Enumerable.Repeat("1", 100_000)),
            string.Concat(Enumerable.Repeat("f(", 100_000)) + "1" + new string(')', 100_000),
        })
        {
            Assert.Throws<SqlSyntaxException>(() => Parse(nest));
        }
    }

    [Fact]
    public void Random_token_sequences_parse_or_are_rejected_cleanly_and_quickly()
    {
        var random = new Random(8675309);
        string[] vocabulary =
        {
            "[a]", "[b]", "t.[c]", "@p", "@q", "1", "2.5", "'s'", "NULL", "AND", "OR", "NOT", "IN", "LIKE", "IS", "(", ")", ",",
            "=", "<>", "<", ">=", "||", "+", "-", "*", "SELECT", "FROM", "AS", "WHERE", "MAX", "[T]", "x", ";",
        };

        var clock = Stopwatch.StartNew();
        for (var round = 0; round < 30_000; round++)
        {
            var sql = string.Join(" ", Enumerable.Range(0, random.Next(0, 24)).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
            try
            {
                Parse(sql);
            }
            catch (SqlSyntaxException)
            {
                // expected for most random input
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"30,000 random expressions took {clock.Elapsed.TotalSeconds:F1} s");
    }
}
