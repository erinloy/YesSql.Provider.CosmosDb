using System.Collections.Generic;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>A parsed statement.</summary>
internal abstract record SqlStatement;

/// <summary>A table or a derived table in a <c>FROM</c> or <c>JOIN</c> clause.</summary>
internal abstract record SqlSource(string? Alias);

internal sealed record NamedSource(string Table, string? Alias) : SqlSource(Alias);

internal sealed record DerivedSource(SelectStatement Query, string? Alias) : SqlSource(Alias);

internal sealed record SelectItem(SqlExpr Expression, string? Alias);

internal enum JoinKind
{
    Inner,
    Left,
    Right,
}

internal sealed record JoinClause(SqlSource Source, SqlExpr On, JoinKind Kind = JoinKind.Inner);

internal sealed record OrderTerm(SqlExpr Expression, bool Descending);

/// <summary>
/// <c>SELECT [DISTINCT] items FROM source [INNER JOIN source ON expr]... [WHERE expr] [GROUP BY ...]
/// [ORDER BY ...] [OFFSET n [ROWS]] [LIMIT n]</c>.
/// </summary>
internal sealed record SelectStatement(
    bool Distinct,
    IReadOnlyList<SelectItem> Items,
    SqlSource? From,
    IReadOnlyList<JoinClause> Joins,
    SqlExpr? Where,
    IReadOnlyList<SqlExpr> GroupBy,
    IReadOnlyList<OrderTerm> OrderBy,
    long? Offset,
    long? Limit) : SqlStatement;

/// <summary><c>INSERT INTO [t] ([c], ...) VALUES (expr, ...) [RETURNING [Id]]</c>; YesSql adds the <c>RETURNING</c> clause to index inserts.</summary>
internal sealed record InsertStatement(string Table, IReadOnlyList<string> Columns, IReadOnlyList<SqlExpr> Values, string? ReturningColumn = null) : SqlStatement;

internal sealed record Assignment(string Column, SqlExpr Value);

internal sealed record UpdateStatement(string Table, IReadOnlyList<Assignment> Assignments, SqlExpr? Where) : SqlStatement;

internal sealed record DeleteStatement(string Table, SqlExpr? Where) : SqlStatement;

/// <summary>The schema command YesSql sends to rename a column: <c>renamecolumn [Table] [From] [To]</c>.</summary>
internal sealed record RenameColumnStatement(string Table, string From, string To) : SqlStatement;
