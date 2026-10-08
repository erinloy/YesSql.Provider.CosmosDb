using YesSql.Provider.CosmosDb.Internal;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

public class SelectShapeTests
{
    private static SelectShape Shape(string sql) => SelectShape.Of(Assert.IsType<SelectStatement>(SqlParser.ParseStatement(sql)), sql);

    // The shape YesSql gives an ordered, paged query over a map index.
    private const string OrderedIndexQuery =
        "SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(PersonByName_a1.[Name]) AS order_1 " +
        "FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] " +
        "WHERE [Document].[Type] = @p AND (PersonByName_a1.[Name] = @p) GROUP BY [Document].[Id] ORDER BY order_1 DESC LIMIT 5 OFFSET 2) " +
        "AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1 DESC";

    [Fact]
    public void An_index_query_is_read_from_inside_its_derived_table()
    {
        var shape = Shape(OrderedIndexQuery);

        Assert.Equal("Document", shape.FromTable);
        Assert.Equal(ReaderRoute.IndexJoin, shape.Reader);
        var join = Assert.Single(shape.IndexJoins);
        Assert.Equal(("PersonByName", "PersonByName_a1"), (join.Table, join.Alias));
        Assert.Equal(2, shape.Offset);
        Assert.Equal(5, shape.Limit);
        Assert.Equal(new[] { new OrderColumn("Name", true) }, shape.Order);
        Assert.NotNull(shape.Where);
        Assert.Equal("DocumentId", Assert.IsType<ColumnRef>(Assert.IsType<BinaryExpr>(shape.LinkJoin!.On).Left).Name);
    }

    [Fact]
    public void The_random_order_clause_is_found_through_its_alias_and_directly()
    {
        var aliased = Shape(
            "SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(GetCurrentTimestamp()) AS order_1 FROM [Document] " +
            "GROUP BY [Document].[Id] ORDER BY order_1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1");
        Assert.Equal(new[] { new OrderColumn(SelectShape.RandomColumn, false) }, aliased.Order);
        Assert.True(aliased.HasRandomOrder);

        var direct = Shape("SELECT * FROM [PersonByName] AS a ORDER BY a.[Name] DESC, GetCurrentTimestamp()");
        Assert.Equal(new[] { new OrderColumn("Name", true), new OrderColumn(SelectShape.RandomColumn, false) }, direct.Order);
    }

    [Fact]
    public void Ordering_by_the_document_id_of_an_index_query_uses_the_document_id_the_index_rows_carry()
    {
        var shape = Shape(
            "SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] " +
            "INNER JOIN [tpPersonByName] AS a ON a.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1) " +
            "AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1");

        Assert.Equal(new[] { new OrderColumn("DocumentId", false) }, shape.Order);
    }

    [Fact]
    public void Ordering_by_the_id_of_a_document_query_keeps_the_column()
    {
        var shape = Shape(
            "SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] " +
            "WHERE [tpDocument].[Type] = @p GROUP BY [tpDocument].[Id] ORDER BY order_1 DESC) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1 DESC");

        Assert.Equal(ReaderRoute.DocumentsByJoin, shape.Reader);
        Assert.Equal(new[] { new OrderColumn("Id", true) }, shape.Order);
        Assert.Equal(" ORDER BY c[\"Id\"] DESC", CosmosDbCommand.OrderByClause(shape.Order));
    }

    [Theory]
    [InlineData("SELECT * FROM [PersonByName] AS a ORDER BY LOWER(a.[Name])")]
    [InlineData("SELECT * FROM [PersonByName] AS a ORDER BY nothing_here")]
    [InlineData("SELECT [Document].* FROM [Document] INNER JOIN [Idx] AS a ON a.[DocumentId] = [Document].[Id] ORDER BY [Document].[Type]")]
    public void An_order_the_provider_cannot_apply_is_refused_and_not_dropped(string sql)
        => Assert.Throws<SqlSyntaxException>(() => Shape(sql).Order);

    [Fact]
    public void A_reduce_index_query_names_the_index_and_its_bridge()
    {
        var shape = Shape(
            "SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] " +
            "INNER JOIN [PersonByName] AS m_a1 ON m_a1.[DocumentId] = [Document].[Id] " +
            "INNER JOIN [ArticlesByDay_Document] AS b_a1 ON b_a1.[DocumentId] = [Document].[Id] " +
            "INNER JOIN [ArticlesByDay] AS i_a1 ON i_a1.[Id] = b_a1.[ArticlesByDayId] " +
            "WHERE (i_a1.[DayOfYear] = @p) GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]");

        Assert.Equal(ReaderRoute.ReduceJoin, shape.Reader);
        Assert.Equal(new ReduceJoin("ArticlesByDay", "i_a1", "b_a1", "ArticlesByDayId", "ArticlesByDay_Document"), shape.Reduce);
    }

    [Fact]
    public void A_reduce_index_query_paged_by_document_id_orders_by_the_document_id()
    {
        var shape = Shape(
            "SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX([Document].[Id]) AS order_1 FROM [Document] " +
            "INNER JOIN [ArticlesByDay_Document] AS b_a1 ON b_a1.[DocumentId] = [Document].[Id] " +
            "INNER JOIN [ArticlesByDay] AS i_a1 ON i_a1.[Id] = b_a1.[ArticlesByDayId] " +
            "WHERE (i_a1.[DayOfYear] = @p) GROUP BY [Document].[Id] ORDER BY order_1 LIMIT 20) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1");

        Assert.Equal(ReaderRoute.ReduceJoin, shape.Reader);
        Assert.Equal(new[] { new OrderColumn("DocumentId", false) }, shape.Order);
        Assert.Equal(20, shape.Limit);
    }

    [Fact]
    public void Joins_to_two_different_index_tables_are_a_multi_index_query_and_the_same_table_twice_is_not()
    {
        const string join = " INNER JOIN [{0}] AS {1} ON {1}.[DocumentId] = [Document].[Id]";
        var two = Shape("SELECT [Document].* FROM [Document]" + string.Format(join, "A", "a1") + string.Format(join, "B", "b1"));
        var same = Shape("SELECT [Document].* FROM [Document]" + string.Format(join, "A", "a1") + string.Format(join, "A", "a2"));

        Assert.Equal(ReaderRoute.MultiIndexJoin, two.Reader);
        Assert.Equal(ReaderRoute.IndexJoin, same.Reader);
    }

    [Fact]
    public void A_hand_written_join_names_the_document_by_an_alias()
    {
        var shape = Shape("SELECT count(1) FROM [tpDocument] AS d INNER JOIN [tpArticleByPublishedDate] AS a ON a.[DocumentId] = d.[Id]");

        Assert.Equal(ScalarRoute.CountJoin, shape.Scalar);
        Assert.Equal("tpArticleByPublishedDate", shape.LinkJoin!.Table);
        Assert.Empty(shape.IndexJoins);
    }

    [Theory]
    [InlineData("SELECT * FROM [Document] WHERE [Id] = @p", "DocumentsById")]
    [InlineData("SELECT * FROM [Document] WHERE [Id] IN (@p, @p)", "DocumentsById")]
    [InlineData("SELECT * FROM [Document] WHERE [Id] NOT IN (@p)", "Documents")]
    [InlineData("SELECT * FROM [Document] WHERE [Type] = @p", "Documents")]
    [InlineData("SELECT * FROM [PersonByName] AS a WHERE a.[Name] = @p", "IndexRows")]
    [InlineData("SELECT count(*) FROM [PersonByName]", "CountRow")]
    [InlineData("SELECT DateTimePart(\"year\", [Published]) FROM [ArticleByDate]", "DatePart")]
    public void A_statement_without_joins_is_routed_by_its_table_and_predicate(string sql, string expected)
        => Assert.Equal(expected, Shape(sql).Reader.ToString());

    [Fact]
    public void Max_of_a_column_is_the_id_seed_only_when_the_statement_itself_selects_it()
    {
        Assert.Equal(ScalarRoute.MaxId, Shape("SELECT MAX([Id]) FROM [Document]").Scalar);
        Assert.Null(Shape(OrderedIndexQuery).Scalar);
    }

    [Fact]
    public void Selecting_something_other_than_columns_from_a_document_table_is_refused()
        => Assert.Throws<SqlSyntaxException>(() => Shape("SELECT MAX([Id]) FROM [Document]").Projection);

    [Fact]
    public void A_projection_lists_its_columns_and_a_star_means_all()
    {
        Assert.Equal(new[] { "Content", "Id" }, Shape("SELECT [Content], [Id] FROM [Document]").Projection);
        Assert.Null(Shape("SELECT [Document].* FROM [Document]").Projection);
        Assert.Null(Shape("SELECT * FROM [Document]").Projection);
    }

    [Fact]
    public void A_type_parameter_is_found_only_where_it_was_removed_from_the_predicate()
    {
        var and = SqlParser.ParseExpression("[Document].[Type] = @t AND (a_a1.[Name] = @p)");
        var or = SqlParser.ParseExpression("[Document].[Type] = @t OR (a_a1.[Name] = @p)");

        Assert.Equal("t", SqlTree.DocumentTypeParameter(and));
        Assert.Null(SqlTree.DocumentTypeParameter(or));
        Assert.Equal("t", SqlTree.DocumentTypeParameter(SqlParser.ParseExpression("([Document].[Type] = @t)")));
    }

    [Theory]
    [InlineData("[Id] = @p", null, false)]
    [InlineData("[Id] = @p AND [Version] = 3", 3L, false)]
    [InlineData("[Id] = @p AND ([Version] IS NULL OR [Version] = 1)", 1L, true)]
    [InlineData("[Id] = @p AND ([Version] = 1 OR [Version] IS NULL)", 1L, true)]
    [InlineData("([Version] = 2) AND [Id] = 7", 2L, false)]
    public void A_single_row_update_condition_is_recognized(string predicate, long? version, bool allowsNull)
    {
        var condition = SqlTree.UpdateCondition(SqlParser.ParseExpression(predicate))!;
        Assert.NotNull(condition);
        Assert.Equal(version, condition.Version);
        Assert.Equal(allowsNull, condition.AllowsNullVersion);
    }

    [Theory]
    [InlineData("[Version] = 3")]
    [InlineData("[Id] = @p AND [Name] = @n")]
    [InlineData("[Id] = @p OR [Id] = @q")]
    [InlineData("[Id] = @p AND [Version] = @v")]
    [InlineData("[Id] = @p AND [Version] = 1 AND [Version] = 2")]
    [InlineData("[Id] > @p")]
    [InlineData("[Id] = @p AND ([Version] IS NULL OR [Version] > 1)")]
    [InlineData("t.[Id] = @p")]
    public void Any_other_update_condition_is_not_recognized(string predicate)
        => Assert.Null(SqlTree.UpdateCondition(SqlParser.ParseExpression(predicate)));

    [Fact]
    public void A_literal_type_is_found_whatever_its_qualifier()
        => Assert.Equal(new LiteralExpr("My.Type"), SqlTree.TypeComparison(SqlParser.ParseExpression("[Document].[Type] = 'My.Type' AND [Id] = @p")));

    [Fact]
    public void A_type_under_an_OR_is_not_a_type_filter()
        => Assert.Null(SqlTree.TypeComparison(SqlParser.ParseExpression("[Type] = 'A' OR [Type] = 'B'")));

    [Theory]
    [InlineData("[Type] = @Type", null)]
    [InlineData("[Document].[Type] = 'A'", null)]
    [InlineData("[Type] = @Type AND [Id] = @Id", "[Id] = @Id")]
    [InlineData("[Type] = @Type AND [Name] LIKE 'a%'", "[Name] LIKE 'a%'")]
    [InlineData("[Type] = @A OR [Type] = @B", "[Type] = @A OR [Type] = @B")]
    public void WithoutTypeComparison_leaves_every_other_condition(string predicate, string? left)
    {
        var remaining = SqlTree.WithoutTypeComparison(SqlParser.ParseExpression(predicate));
        Assert.Equal(left is null ? null : SqlPrinter.Print(SqlParser.ParseExpression(left)), remaining is null ? null : SqlPrinter.Print(remaining));
    }

    [Theory]
    [InlineData("[Id] = @Id", 1)]
    [InlineData("[Id] IN (@Id1, @Id2, 7)", 3)]
    [InlineData("([Id] = @Id)", 1)]
    public void A_predicate_that_selects_by_key_gives_its_operands(string predicate, int count)
        => Assert.Equal(count, SqlTree.KeyOperands(SqlParser.ParseExpression(predicate))!.Count);

    [Theory]
    [InlineData("[Id] = @Id AND [Type] = @Type")]
    [InlineData("[Id] IN (SELECT [Id] FROM [T] AS t)")]
    [InlineData("[Id] IN (@Id1, [Other])")]
    [InlineData("[Id] > @Id")]
    [InlineData("[Id] = 'x'")]
    public void Any_other_predicate_does_not_select_by_key(string predicate)
        => Assert.Null(SqlTree.KeyOperands(SqlParser.ParseExpression(predicate)));

    private static readonly Dictionary<string, string> Tables = new() { ["a"] = "One", ["b"] = "Two", ["c"] = "One" };

    [Fact]
    public void Terms_are_split_over_the_tables_they_refer_to()
    {
        var split = SqlTree.SplitByTable(SqlParser.ParseExpression("a.[Name] = @n AND b.[Age] > @a AND a.[City] = @c"), Tables, "sql");
        Assert.Equal(2, split["One"].Count);
        Assert.Single(split["Two"]);
    }

    [Fact]
    public void Aliases_of_one_table_are_one_target()
    {
        // The same index joined twice: a condition over both aliases is tested against the rows of the one table.
        var split = SqlTree.SplitByTable(SqlParser.ParseExpression("(a.[Day] = @x OR c.[Day] = @y)"), Tables, "sql");
        Assert.Single(split["One"]);
        Assert.Empty(split["Two"]);
    }

    [Fact]
    public void An_unqualified_term_belongs_to_the_only_table()
        => Assert.Single(SqlTree.SplitByTable(SqlParser.ParseExpression("[Name] = @n"), new Dictionary<string, string> { ["a"] = "One", ["c"] = "One" }, "sql")["One"]);

    [Theory]
    [InlineData("[Name] = @n")]
    [InlineData("a.[Name] = b.[Name]")]
    [InlineData("a.[Name] = @n OR b.[Name] = @n")]
    [InlineData("[Document].[Version] = 3")]
    [InlineData("z.[Name] = @n")]
    public void A_term_that_cannot_be_run_against_one_table_is_refused(string predicate)
        => Assert.Throws<NotSupportedException>(() => SqlTree.SplitByTable(SqlParser.ParseExpression(predicate), Tables, "sql"));

    [Fact]
    public void A_term_over_a_subquery_belongs_to_the_alias_of_its_operand()
    {
        var split = SqlTree.SplitByTable(SqlParser.ParseExpression("a.[DocumentId] IN (SELECT s.[DocumentId] FROM [T] AS s WHERE s.[X] = @x)"), Tables, "sql");
        Assert.Single(split["One"]);
    }

    [Fact]
    public void Equality_operands_are_found_by_column()
    {
        var operands = SqlTree.EqualityOperands(SqlParser.ParseExpression("[nextval] = @previous AND [dimension] = @dimension"))!;
        Assert.Equal(new ParamRef("previous"), operands["NEXTVAL"]);
        Assert.Equal(new ParamRef("dimension"), operands["dimension"]);
        Assert.Null(SqlTree.EqualityOperands(SqlParser.ParseExpression("[a] = @a AND [b] > @b")));
        Assert.Null(SqlTree.EqualityOperands(SqlParser.ParseExpression("[a] = @a AND [a] = @b")));
    }
}
