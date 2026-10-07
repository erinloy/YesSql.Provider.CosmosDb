using System.Diagnostics;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Tests.Sql;

public class SqlLexerTests
{
    private static string[] Corpus() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Sql", "statements.sql"))
            .Where(line => line.Length > 0)
            .ToArray();

    private static List<(SqlTokenKind Kind, string Value)> Lex(string sql)
        => SqlLexer.Tokenize(sql).Select(t => (t.Kind, t.Value)).ToList();

    [Fact]
    public void Every_statement_in_the_corpus_is_tokenized_without_losing_any_text()
    {
        var corpus = Corpus();
        Assert.True(corpus.Length > 250, "the statement corpus is missing or truncated");

        foreach (var sql in corpus)
        {
            var tokens = SqlLexer.Tokenize(sql);
            Assert.NotEmpty(tokens);

            // Each token is the exact source slice, in order, and only whitespace lies between tokens.
            var cursor = 0;
            foreach (var token in tokens)
            {
                Assert.True(token.Position >= cursor, $"tokens overlap in: {sql}");
                Assert.True(string.IsNullOrWhiteSpace(sql[cursor..token.Position]), $"text skipped before {token} in: {sql}");
                Assert.Equal(token.Text, sql.Substring(token.Position, token.Text.Length));
                cursor = token.End;
            }

            Assert.True(string.IsNullOrWhiteSpace(sql[cursor..]), $"trailing text skipped in: {sql}");
        }
    }

    [Fact]
    public void Identifiers_parameters_and_literals_have_the_right_kinds_and_values()
    {
        var tokens = Lex("SELECT [Document].[Id] FROM [Document] WHERE [Type] = @p0 AND [Name] = 'a b' AND [N] <= 12.5");

        Assert.Equal((SqlTokenKind.Word, "SELECT"), tokens[0]);
        Assert.Equal((SqlTokenKind.Bracketed, "Document"), tokens[1]);
        Assert.Equal((SqlTokenKind.Symbol, "."), tokens[2]);
        Assert.Contains((SqlTokenKind.Parameter, "p0"), tokens);
        Assert.Contains((SqlTokenKind.String, "a b"), tokens);
        Assert.Contains((SqlTokenKind.Symbol, "<="), tokens);
        Assert.Equal((SqlTokenKind.Number, "12.5"), tokens[^1]);
    }

    [Fact]
    public void A_backslash_is_an_ordinary_character_and_a_doubled_quote_is_one_quote()
    {
        // The statement shape that broke the regex translator: a literal ending in a backslash.
        var tokens = SqlLexer.Tokenize(@"[x] = 'it''s a \'");

        Assert.Equal(3, tokens.Count);
        Assert.Equal(SqlTokenKind.String, tokens[2].Kind);
        Assert.Equal(@"it's a \", tokens[2].Value);
    }

    [Fact]
    public void A_closing_bracket_can_be_escaped_by_doubling_it()
    {
        var token = Assert.Single(SqlLexer.Tokenize("[a]]b]"));
        Assert.Equal("a]b", token.Value);
    }

    [Fact]
    public void Multi_character_operators_are_single_tokens()
    {
        var symbols = SqlLexer.Tokenize("a <> b <= c >= d != e || f").Where(t => t.Kind == SqlTokenKind.Symbol).Select(t => t.Text);
        Assert.Equal(new[] { "<>", "<=", ">=", "!=", "||" }, symbols);
    }

    [Fact]
    public void Keywords_match_without_regard_to_case()
    {
        var token = Assert.Single(SqlLexer.Tokenize("sElEcT"));
        Assert.True(token.IsWord("SELECT"));
        Assert.False(token.IsWord("SELECTED"));
    }

    [Theory]
    [InlineData("SELECT [Id FROM x", "Unterminated [identifier]")]
    [InlineData("SELECT [] FROM x", "Empty [identifier]")]
    [InlineData("WHERE a = 'abc", "Unterminated")]
    [InlineData("SELECT DateTimePart(\"year, [d])", "Unterminated")]
    [InlineData("WHERE a = @ AND b", "parameter name")]
    [InlineData("WHERE a = 12abc", "number")]
    [InlineData("WHERE a # b", "Unexpected character '#'")]
    [InlineData("WHERE a = `b`", "Unexpected character '`'")]
    public void Malformed_input_is_rejected_with_its_position(string sql, string expected)
    {
        var ex = Assert.Throws<SqlSyntaxException>(() => SqlLexer.Tokenize(sql));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(ex.Position, 0, sql.Length);
        Assert.IsAssignableFrom<NotSupportedException>(ex);
    }

    [Fact]
    public void Cost_grows_linearly_with_statement_length()
    {
        static string InList(int count) =>
            "SELECT * FROM [Document] WHERE [Id] IN (" + string.Join(", ", Enumerable.Range(0, count).Select(i => "@p" + i)) + ")";

        // warm up
        SqlLexer.Tokenize(InList(1_000));

        var small = Stopwatch.StartNew();
        SqlLexer.Tokenize(InList(20_000));
        var smallMs = small.Elapsed.TotalMilliseconds;

        var large = Stopwatch.StartNew();
        var tokens = SqlLexer.Tokenize(InList(200_000));
        var largeMs = large.Elapsed.TotalMilliseconds;

        // 8 tokens before the list, 200,000 parameters, 199,999 commas and the closing parenthesis.
        Assert.Equal(400_008, tokens.Count);
        Assert.True(largeMs < 2_000, $"200,000 parameters took {largeMs:F0} ms");
        Assert.True(largeMs < smallMs * 30, $"10x the input took {largeMs / Math.Max(smallMs, 0.01):F1}x the time");
    }

    [Fact]
    public void Random_input_either_tokenizes_or_is_rejected_cleanly_and_quickly()
    {
        var random = new Random(20261006);
        const string alphabet = "abcXYZ019 _\t\n[]@'\"()=<>!|,.;*+-/%#$`\\{}~^&?:";
        var clock = Stopwatch.StartNew();

        for (var round = 0; round < 20_000; round++)
        {
            var length = random.Next(0, 80);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var sql = new string(chars);
            try
            {
                var tokens = SqlLexer.Tokenize(sql);
                Assert.All(tokens, t => Assert.Equal(t.Text, sql.Substring(t.Position, t.Text.Length)));
            }
            catch (SqlSyntaxException)
            {
                // expected for most random input
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"20,000 random statements took {clock.Elapsed.TotalSeconds:F1} s");
    }
}
