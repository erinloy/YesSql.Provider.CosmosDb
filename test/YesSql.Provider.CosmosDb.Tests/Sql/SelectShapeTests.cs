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

    [Fact]
    public void Version_checks_are_read_from_the_predicate()
    {
        var check = SqlParser.ParseExpression("[Id] = @p AND ([Version] = 3 OR [Version] IS NULL)");
        var plain = SqlParser.ParseExpression("[Id] = @p");

        Assert.Equal(3, SqlTree.VersionCheck(check));
        Assert.True(SqlTree.AllowsNullVersion(check));
        Assert.Null(SqlTree.VersionCheck(plain));
        Assert.False(SqlTree.AllowsNullVersion(plain));
    }

    [Fact]
    public void A_literal_type_is_found_whatever_its_qualifier()
        => Assert.Equal("My.Type", SqlTree.TypeLiteral(SqlParser.ParseExpression("[Document].[Type] = 'My.Type' AND [Id] = @p")));
}
