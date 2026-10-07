using System.Text.RegularExpressions;
using YesSql.Provider.CosmosDb.Internal;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

public class CosmosExpressionWriterTests
{
    private static (string Text, List<string> Literals, List<InSubqueryExpr> Subqueries) Write(string sql, params string[] dateParameters)
    {
        var literals = new List<string>();
        var subqueries = new List<InSubqueryExpr>();
        var writer = new CosmosExpressionWriter(
            subquery => { subqueries.Add(subquery); return "@__sq" + (subqueries.Count - 1); },
            text => { literals.Add(text); return "@__lit" + (literals.Count - 1); },
            new HashSet<string>(dateParameters));
        return (writer.Write(SqlParser.ParseExpression(sql)), literals, subqueries);
    }

    [Fact]
    public void Columns_are_written_as_properties_and_qualifiers_are_dropped()
    {
        Assert.Equal("c[\"Id\"] = @p", Write("[Document].[Id] = @p").Text);
        Assert.Equal("c[\"SomeName\"] = @p0 AND c[\"Age\"] > 3", Write("PersonByName_a1.[SomeName] = @p0 and [Age] > 3").Text);
    }

    [Fact]
    public void Parentheses_and_logical_operators_are_kept()
        => Assert.Equal("(c[\"a\"] = @p OR c[\"b\"] = @q) AND NOT c[\"c\"] = 1",
            Write("([a] = @p or [b] = @q) and not [c] = 1").Text);

    [Fact]
    public void Null_tests_use_the_Cosmos_functions()
    {
        Assert.Equal("(NOT IS_DEFINED(c[\"a\"]) OR IS_NULL(c[\"a\"]))", Write("x.[a] IS NULL").Text);
        Assert.Equal("(IS_DEFINED(c[\"a\"]) AND NOT IS_NULL(c[\"a\"]))", Write("x.[a] IS NOT NULL").Text);
        Assert.Throws<SqlSyntaxException>(() => Write("@p IS NULL"));
    }

    [Fact]
    public void String_literals_become_parameters_so_their_content_cannot_change_the_query()
    {
        // A literal ending in a backslash, or containing a quote and Cosmos query syntax, is never written as text.
        var (text, literals, _) = Write(@"[a] = 'x\' OR [b] = 'it''s'' OR 1 = 1 --'");

        Assert.Equal("c[\"a\"] = @__lit0 OR c[\"b\"] = @__lit1", text);
        Assert.Equal(new[] { @"x\", "it's' OR 1 = 1 --" }, literals);
    }

    [Fact]
    public void Not_like_is_written_as_an_operator_and_not_as_a_prefix()
        => Assert.Equal("(c[\"Name\"] NOT LIKE @p)", Write("(PersonByAge_a1.[Name] not like @p)").Text);

    [Fact]
    public void Numbers_lists_and_null_are_written_directly()
        => Assert.Equal("c[\"a\"] IN (@p0, 2, @__lit0) AND c[\"b\"] NOT IN (1.5) AND c[\"c\"] = null",
            Write("[a] IN (@p0, 2, 's') AND [b] NOT IN (1.5) AND [c] = NULL").Text);

    [Fact]
    public void A_date_column_compared_with_a_date_parameter_is_compared_by_instant_in_either_order()
    {
        Assert.Equal("DateTimeToTimestamp(c[\"d\"]) >= DateTimeToTimestamp(@when) AND c[\"n\"] = @other",
            Write("[d] >= @when AND [n] = @other", "when").Text);
        Assert.Equal("DateTimeToTimestamp(@when) < DateTimeToTimestamp(c[\"d\"])",
            Write("@when < [d]", "when").Text);
        Assert.Equal("c[\"d\"] = @when", Write("[d] = @when").Text);
    }

    [Fact]
    public void An_in_subquery_is_resolved_by_the_caller_and_becomes_ARRAY_CONTAINS()
    {
        var (text, _, subqueries) = Write(
            "x_a1.[Nick] IN (SELECT y_a1.[Name] FROM [PersonByName] AS y_a1 WHERE y_a1.[Name] like @p) AND x_a1.[Age] NOT IN (SELECT z.[Age] FROM [Old] AS z)");

        Assert.Equal("ARRAY_CONTAINS(@__sq0, c[\"Nick\"]) AND NOT ARRAY_CONTAINS(@__sq1, c[\"Age\"])", text);
        Assert.Equal(new[] { "PersonByName", "Old" }, subqueries.Select(s => s.Query.Table));
    }

    [Fact]
    public void Concatenation_functions_and_arithmetic_are_passed_through()
        => Assert.Equal("(c[\"a\"] || @p || c[\"b\"]) = @q AND MAX(c[\"n\"]) > 2 + 3 * 4",
            Write("([a] || @p || [b]) = @q AND MAX([n]) > 2 + 3 * 4").Text);

    [Fact]
    public void A_column_name_that_could_end_the_quoted_property_is_refused()
    {
        Assert.Throws<SqlSyntaxException>(() => Write("[a\"] = 1"));
        Assert.Throws<SqlSyntaxException>(() => Write(@"[a\b] = 1"));
    }

    [Fact]
    public void A_very_long_chain_is_written_without_recursion_problems()
    {
        var sql = string.Join(" OR ", Enumerable.Range(0, 50_000).Select(i => $"[a] = @p{i}"));
        var text = Write(sql).Text;
        Assert.StartsWith("c[\"a\"] = @p0 OR c[\"a\"] = @p1", text);
        Assert.EndsWith("c[\"a\"] = @p49999", text);
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", string.Empty).ToLowerInvariant();

    private static string[] Corpus() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Sql", "statements.sql")).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void The_new_translation_equals_the_old_one_on_every_where_clause_in_the_corpus()
    {
        var legacy = new CosmosDbCommand(null!);
        var compared = 0;
        var rejectedOnPurpose = 0;
        var notComparable = new List<string>();
        var differences = new List<string>();

        foreach (var sql in Corpus())
        {
            var where = CosmosDbCommand.ExtractWhere(sql);
            if (string.IsNullOrWhiteSpace(where))
            {
                continue;
            }

            // The old translator resolves a subquery by running it against Cosmos, so those are covered by the
            // writer test above instead.
            if (Regex.IsMatch(where, @"\bin\s*\(\s*select\b", RegexOptions.IgnoreCase))
            {
                notComparable.Add("subquery: " + where);
                continue;
            }

            var oldText = legacy.TranslateWhere(where);

            string newText;
            try
            {
                var literals = new List<string>();
                var writer = new CosmosExpressionWriter(_ => throw new InvalidOperationException(), text => { literals.Add(text); return "@__lit" + (literals.Count - 1); });
                newText = writer.Write(SqlParser.ParseExpression(where));
                for (var i = 0; i < literals.Count; i++)
                {
                    newText = newText.Replace("@__lit" + i, "'" + literals[i].Replace("'", "''") + "'");
                }
            }
            catch (SqlSyntaxException ex)
            {
                // YesSql's tests send a bare, unquoted column on purpose, to check that a rejected query raises a
                // DbException. The old translator left it for Cosmos to reject; the parser rejects it up front.
                if (where.Contains("ThisColumnDoesNotExist", StringComparison.Ordinal))
                {
                    rejectedOnPurpose++;
                    continue;
                }

                differences.Add($"REJECTED  {where}\n          {ex.Message}");
                continue;
            }

            compared++;
            if (Normalize(oldText) != Normalize(newText))
            {
                differences.Add($"DIFFERENT {where}\n  old: {oldText}\n  new: {newText}");
            }
        }

        Assert.True(compared > 60, $"only {compared} where clauses were compared");
        Assert.True(rejectedOnPurpose >= 3, "the unquoted-column statements should be rejected by the parser");
        Assert.True(differences.Count == 0, $"{differences.Count} of {compared + differences.Count} differ:\n" + string.Join("\n", differences.Take(15)));
    }
}
