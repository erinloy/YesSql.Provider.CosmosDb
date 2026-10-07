using System;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>
/// Thrown when the provider rejects a statement because it is not in the SQL subset it understands. It is a
/// <see cref="DbException"/>, like the error a relational database raises for invalid SQL, because YesSql 6 expects a
/// rejected query to surface as one.
/// </summary>
internal sealed class SqlSyntaxException : DbException
{
    public SqlSyntaxException(string message, string statement, int position)
        : base($"{message} at position {position} in: {Excerpt(statement, position)}")
    {
        Statement = statement;
        Position = position;
    }

    /// <summary>For a rejection that is found after parsing, when no single position applies.</summary>
    public SqlSyntaxException(string message)
        : base(message)
    {
        Statement = string.Empty;
        Position = 0;
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
