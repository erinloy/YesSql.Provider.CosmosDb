using System;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>
/// Thrown when a statement is not in the SQL subset the provider understands. It derives from
/// <see cref="NotSupportedException"/> because that is what the provider throws for statements it cannot translate.
/// </summary>
internal sealed class SqlSyntaxException : NotSupportedException
{
    public SqlSyntaxException(string message, string statement, int position)
        : base($"{message} at position {position} in: {Excerpt(statement, position)}")
    {
        Statement = statement;
        Position = position;
    }

    public string Statement { get; }

    public int Position { get; }

    // A statement can be tens of kilobytes (a long IN list), so the message shows only the neighbourhood of the error.
    private static string Excerpt(string statement, int position)
    {
        const int radius = 60;
        var start = Math.Max(0, Math.Min(position, statement.Length) - radius);
        var end = Math.Min(statement.Length, Math.Min(position, statement.Length) + radius);
        var text = statement.Substring(start, end - start);
        return (start > 0 ? "..." : string.Empty) + text + (end < statement.Length ? "..." : string.Empty);
    }
}
