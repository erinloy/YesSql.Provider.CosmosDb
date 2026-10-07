using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

public class SqlTreeTests
{
    private static SqlExpr Parse(string sql) => SqlParser.ParseExpression(sql);

    private static string Print(SqlExpr? expression) => expression is null ? "(nothing)" : SqlPrinter.Print(expression);

    [Fact]
    public void Conjuncts_unwrap_outer_parentheses_and_split_on_top_level_and()
    {
        Assert.Equal(new[] { "[a] = 1", "([b] = 2 OR [c] = 3)", "[d] = 4" },
            SqlTree.Conjuncts(Parse("(([a] = 1 AND ([b] = 2 OR [c] = 3) AND [d] = 4))")).Select(Print));

        Assert.Single(SqlTree.Conjuncts(Parse("[a] = 1 OR [b] = 2")));
        Assert.Single(SqlTree.Conjuncts(Parse("[a] = 1")));
        Assert.Empty(SqlTree.Conjuncts(null));
    }

    [Fact]
    public void And_is_the_inverse_of_Conjuncts()
    {
        Assert.Null(SqlTree.And(Array.Empty<SqlExpr>()));
        Assert.Equal("[a] = 1", Print(SqlTree.And(SqlTree.Conjuncts(Parse("[a] = 1")))));
        Assert.Equal("[a] = 1 AND [b] = 2", Print(SqlTree.And(SqlTree.Conjuncts(Parse("([a] = 1 AND [b] = 2)")))));
    }

    [Theory]
    [InlineData("[tpDocument].[Type] = @p AND (a_a1.[Name] = @q)", "(a_a1.[Name] = @q)")]
    [InlineData("a_a1.[Name] = @q AND [Document].[Type] = @p", "a_a1.[Name] = @q")]
    [InlineData("[Document].[Type] = @p", "(nothing)")]
    [InlineData("([Document].[Type] = @p AND a_a1.[Name] = @q)", "(a_a1.[Name] = @q)")]
    [InlineData("[Document].[Type] = @p AND a_a1.[x] = 1 AND b_a1.[y] = 2", "a_a1.[x] = 1 AND b_a1.[y] = 2")]
    public void The_document_type_predicate_is_removed_from_an_and_chain(string where, string expected)
        => Assert.Equal(expected, Print(SqlTree.WithoutDocumentTypePredicate(Parse(where))));

    [Theory]
    [InlineData("a_a1.[Type] = @p")]                       // an index column that happens to be called Type
    [InlineData("[Document].[Type] = 'literal'")]          // not compared with a parameter
    [InlineData("[Document].[Type] <> @p")]
    [InlineData("[Document].[Type] = @p OR a_a1.[x] = 1")]  // OR must never lose a term
    [InlineData("NOT [Document].[Type] = @p")]
    public void Other_predicates_are_left_alone(string where)
        => Assert.Equal(Print(Parse(where)), Print(SqlTree.WithoutDocumentTypePredicate(Parse(where))));

    [Fact]
    public void Qualifiers_are_collected_from_every_column_including_subqueries()
    {
        var qualifiers = SqlTree.Qualifiers(Parse(
            "a_a1.[x] = 1 AND (b_a1.[y] = @p OR c_a1.[z] IN (SELECT d_a1.[w] FROM [T] AS d_a1 WHERE e_a1.[v] = 1)) AND [Id] = 2"));

        Assert.Equal(new[] { "a_a1", "b_a1", "c_a1", "d_a1", "e_a1" }, qualifiers.OrderBy(q => q));
        Assert.True(SqlTree.Qualifiers(Parse("A_A1.[x] = 1")).Contains("a_a1"));
    }

    [Fact]
    public void Subqueries_finds_the_outermost_ones_only()
    {
        var found = SqlTree.Subqueries(Parse(
            "[a] IN (SELECT t.[b] FROM [T] AS t WHERE t.[c] IN (SELECT u.[d] FROM [U] AS u)) AND [e] NOT IN (SELECT v.[f] FROM [V] AS v)"));

        Assert.Equal(new[] { "T", "V" }, found.Select(s => s.Query.Table));
    }

    [Fact]
    public void Walking_a_very_long_chain_does_not_use_the_call_stack()
    {
        var chain = string.Join(" AND ", Enumerable.Range(0, 100_000).Select(i => $"a_a1.[c{i}] = @p{i}"));
        Assert.Single(SqlTree.Qualifiers(Parse(chain)));
    }
}
