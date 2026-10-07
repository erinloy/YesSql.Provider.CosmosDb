using System.Diagnostics;
using System.Text.RegularExpressions;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

public class SqlParserStatementTests
{
    private static string[] Corpus() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Sql", "statements.sql")).Where(l => l.Length > 0).ToArray();

    // Case, whitespace, bracket style, semicolons and the order of OFFSET and LIMIT carry no meaning here.
    private static string Normalize(string sql)
    {
        var text = Regex.Replace(sql.ToLowerInvariant(), @"[\s\[\];]", string.Empty).Replace("innerjoin", "join");
        var paging = Regex.Matches(text, @"limit\d+|offset\d+(?:rows)?").Select(m => m.Value.Replace("rows", string.Empty)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return Regex.Replace(text, @"limit\d+|offset\d+(?:rows)?", string.Empty) + "|" + string.Join(",", paging);
    }

    private static SelectStatement Select(string sql) => Assert.IsType<SelectStatement>(SqlParser.ParseStatement(sql));

    [Fact]
    public void Every_statement_in_the_corpus_parses_and_prints_back_to_the_same_statement()
    {
        var parsed = 0;
        var rejected = new List<string>();
        var mismatches = new List<string>();

        foreach (var sql in Corpus())
        {
            SqlStatement statement;
            try
            {
                statement = SqlParser.ParseStatement(sql);
            }
            catch (SqlSyntaxException ex)
            {
                rejected.Add(sql + "\n      -> " + ex.Message);
                continue;
            }

            parsed++;
            var printed = SqlPrinter.Print(statement);
            if (Normalize(printed) != Normalize(sql))
            {
                mismatches.Add($"{sql}\n   printed: {printed}");
            }
        }

        // YesSql's tests send a bare column, and a renamecolumn with one column, to check that bad input is refused.
        var unexpected = rejected.Where(r => !r.Contains("ThisColumnDoesNotExist") && !r.StartsWith("renamecolumn [Table] [OnlyOneColumn]")).ToList();
        Assert.True(unexpected.Count == 0, $"{unexpected.Count} statements were rejected:\n" + string.Join("\n", unexpected.Take(12)));
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} statements changed when parsed and printed:\n" + string.Join("\n", mismatches.Take(8)));
        Assert.True(parsed > 290, $"only {parsed} statements parsed");
    }

    [Fact]
    public void An_index_join_through_a_derived_table_has_the_expected_structure()
    {
        var select = Select(
            "SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] " +
            "INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] " +
            "WHERE [tpDocument].[Type] = @Type AND (PersonByName_a1.[SomeName] = @p0) GROUP BY [tpDocument].[Id]) AS indexQuery " +
            "ON indexQuery.[Id] = [tpDocument].[Id] LIMIT 10");

        Assert.Equal(new StarExpr("tpDocument"), Assert.Single(select.Items).Expression);
        Assert.Equal(new NamedSource("tpDocument", null), select.From);
        Assert.Equal(10, select.Limit);

        var join = Assert.Single(select.Joins);
        var derived = Assert.IsType<DerivedSource>(join.Source);
        Assert.Equal("indexQuery", derived.Alias);
        Assert.Single(derived.Query.Joins);
        Assert.Single(derived.Query.GroupBy);
        Assert.IsType<LogicalExpr>(derived.Query.Where);
    }

    [Fact]
    public void Counts_aggregates_ordering_and_paging_are_read()
    {
        var count = Select("SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument]");
        var function = Assert.IsType<FunctionExpr>(Assert.Single(count.Items).Expression);
        Assert.True(function.Distinct);

        var star = Select("select count(*) from [T] as t where (t.[a] is null)");
        Assert.Equal(new StarExpr(null), Assert.Single(Assert.IsType<FunctionExpr>(Assert.Single(star.Items).Expression).Arguments));
        Assert.Equal("t", star.From!.Alias);

        var ordered = Select("SELECT [Document].[Id], MAX(a.[Name]) AS order_1 FROM [Document] GROUP BY [Document].[Id] ORDER BY order_1 DESC, [Id] OFFSET 5 LIMIT 10");
        Assert.Equal("order_1", ordered.Items[1].Alias);
        Assert.Equal(new AliasRefExpr("order_1"), ordered.OrderBy[0].Expression);
        Assert.True(ordered.OrderBy[0].Descending);
        Assert.False(ordered.OrderBy[1].Descending);
        Assert.Equal((5L, 10L), (ordered.Offset!.Value, ordered.Limit!.Value));

        Assert.Equal((3L, 7L), (Select("select * from [T] limit 7 offset 3").Offset!.Value, Select("select * from [T] limit 7 offset 3").Limit!.Value));
        Assert.Equal(4L, Select("select * from [T] offset 4 rows").Offset);

        var part = Assert.IsType<FunctionExpr>(Assert.Single(Select("SELECT DateTimePart(\"year\", [Date]) FROM [T]").Items).Expression);
        Assert.Equal(new QuotedNameExpr("year"), part.Arguments[0]);
    }

    [Fact]
    public void Join_kinds_are_read()
    {
        var kinds = Select("SELECT * FROM [A] AS a JOIN [B] AS b ON 1 = 1 INNER JOIN [C] AS c ON 1 = 1 LEFT JOIN [D] AS d ON 1 = 1 LEFT OUTER JOIN [E] AS e ON 1 = 1 RIGHT JOIN [F] AS f ON 1 = 1")
            .Joins.Select(j => j.Kind);

        Assert.Equal(new[] { JoinKind.Inner, JoinKind.Inner, JoinKind.Left, JoinKind.Left, JoinKind.Right }, kinds);
    }

    [Fact]
    public void Writes_and_schema_commands_are_read()
    {
        var insert = Assert.IsType<InsertStatement>(SqlParser.ParseStatement("INSERT INTO [tpDocument] ([Id], [Type], [Content], [Version]) VALUES (@Id, @Type, @Content, @Version);"));
        Assert.Equal("tpDocument", insert.Table);
        Assert.Equal(new[] { "Id", "Type", "Content", "Version" }, insert.Columns);
        Assert.Equal(4, insert.Values.Count);
        Assert.Null(insert.ReturningColumn);

        var returning = Assert.IsType<InsertStatement>(SqlParser.ParseStatement("insert into [tpIdx] ([Name], [DocumentId]) values (@Name, @DocumentId) RETURNING [Id];"));
        Assert.Equal("Id", returning.ReturningColumn);

        var update = Assert.IsType<UpdateStatement>(SqlParser.ParseStatement("update [Document] set [Content] = @p, [Version] = @q where [Id] = @i and [Version] = 3 ;"));
        Assert.Equal(2, update.Assignments.Count);
        Assert.IsType<LogicalExpr>(update.Where);

        var replace = Assert.IsType<UpdateStatement>(SqlParser.ParseStatement("UPDATE [Document] SET [Content] = REPLACE([Content], 'a', @b) WHERE [Type] = 'T'"));
        Assert.Equal("REPLACE", Assert.IsType<FunctionExpr>(replace.Assignments[0].Value).Name);

        var delete = Assert.IsType<DeleteStatement>(SqlParser.ParseStatement("DELETE FROM [Document] WHERE [Id] IN (@a, @b)"));
        Assert.IsType<InListExpr>(delete.Where);
        Assert.Null(Assert.IsType<DeleteStatement>(SqlParser.ParseStatement("DELETE FROM [Document]")).Where);

        Assert.Equal(new RenameColumnStatement("T", "A", "B"), SqlParser.ParseStatement("renamecolumn [T] [A] [B]"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("DROP TABLE [T]", "Unsupported statement starting with DROP")]
    [InlineData("SELECT * FROM [T] WHERE", "statement ended")]
    [InlineData("SELECT * FROM [T] FULL JOIN [U] AS u ON 1 = 1", "Unexpected FULL")]
    [InlineData("SELECT * FROM [T] CROSS JOIN [U] AS u", "Unexpected CROSS")]
    [InlineData("SELECT * FROM T", "Expected a [table]")]
    [InlineData("SELECT * FROM [T] LIMIT @p", "LIMIT needs a whole number")]
    [InlineData("SELECT * FROM [T] LIMIT 1.5", "LIMIT needs a whole number")]
    [InlineData("SELECT * FROM [T] LIMIT 1 LIMIT 2", "Unexpected LIMIT")]
    [InlineData("SELECT * FROM (SELECT 1) ", "Expected AS")]
    [InlineData("SELECT * FROM [T] INNER [U]", "Expected JOIN")]
    [InlineData("INSERT INTO [T] ([a], [b]) VALUES (@p)", "2 columns but supplies 1 values")]
    [InlineData("INSERT INTO [T] VALUES (@p)", "Expected '('")]
    [InlineData("UPDATE [T] SET a = 1", "Expected a [column]")]
    [InlineData("DELETE [T]", "Expected FROM")]
    [InlineData("renamecolumn [T] [A]", "statement ended")]
    [InlineData("SELECT * FROM [T]; SELECT * FROM [U]", "Unexpected SELECT after the statement")]
    public void Unsupported_or_malformed_statements_are_rejected_with_a_position(string sql, string expected)
    {
        var ex = Assert.Throws<SqlSyntaxException>(() => SqlParser.ParseStatement(sql));
        Assert.Contains(expected, ex.Message);
        Assert.InRange(ex.Position, 0, sql.Length);
    }

    [Fact]
    public void Deeply_nested_derived_tables_are_refused_without_overflowing_the_stack()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT * FROM (", 50_000)) + "SELECT 1" + string.Concat(Enumerable.Repeat(") AS t", 50_000));
        Assert.Throws<SqlSyntaxException>(() => SqlParser.ParseStatement(sql));
    }

    [Fact]
    public void A_statement_with_a_very_long_select_list_and_in_list_parses_in_linear_time()
    {
        var items = string.Join(", ", Enumerable.Range(0, 20_000).Select(i => $"[c{i}]"));
        var ids = string.Join(", ", Enumerable.Range(0, 100_000).Select(i => $"@p{i}"));

        var clock = Stopwatch.StartNew();
        var select = Select($"SELECT {items} FROM [T] WHERE [Id] IN ({ids})");
        Assert.Equal(20_000, select.Items.Count);
        Assert.Equal(100_000, Assert.IsType<InListExpr>(select.Where).Items.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"parsing took {clock.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void Random_keyword_sequences_parse_or_are_rejected_cleanly_and_quickly()
    {
        var random = new Random(31337);
        string[] vocabulary =
        {
            "SELECT", "DISTINCT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "LIMIT", "OFFSET", "INNER", "JOIN", "ON", "AS", "INSERT", "INTO",
            "VALUES", "UPDATE", "SET", "DELETE", "renamecolumn", "[T]", "[a]", "t.[b]", "t", "*", "t.*", "@p", "1", "'s'", "NULL", "AND", "OR",
            "NOT", "IN", "(", ")", ",", "=", ";", "DESC", "count", "MAX", "ASC",
        };

        var clock = Stopwatch.StartNew();
        for (var round = 0; round < 30_000; round++)
        {
            var sql = string.Join(" ", Enumerable.Range(0, random.Next(0, 30)).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
            try
            {
                SqlParser.ParseStatement(sql);
            }
            catch (SqlSyntaxException)
            {
                // expected for most random input
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"30,000 random statements took {clock.Elapsed.TotalSeconds:F1} s");
    }
}
