using System.Collections.Generic;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

/// <summary>A node of a parsed SQL expression. The tree keeps explicit parentheses as <see cref="ParenExpr"/> nodes.</summary>
internal abstract record SqlExpr;

/// <summary>
/// <c>[Name]</c>, <c>[Qualifier].[Name]</c> or <c>alias.[Name]</c>. <see cref="QualifierIsTable"/> is true when the
/// qualifier was written in brackets, which is how YesSql names a table, as opposed to a bare alias.
/// </summary>
internal sealed record ColumnRef(string? Qualifier, string Name, bool QualifierIsTable = false) : SqlExpr;

/// <summary><c>@name</c>; <see cref="Name"/> excludes the <c>@</c>.</summary>
internal sealed record ParamRef(string Name) : SqlExpr;

/// <summary>A number, a <c>'string'</c>, <c>TRUE</c>, <c>FALSE</c> or <c>NULL</c>. <see cref="Value"/> is a <see cref="long"/>, a <see cref="decimal"/>, a <see cref="string"/>, a <see cref="bool"/> or null.</summary>
internal sealed record LiteralExpr(object? Value) : SqlExpr;

/// <summary>A comparison, <c>LIKE</c>, <c>NOT LIKE</c> or an arithmetic or concatenation operator, in upper case.</summary>
internal sealed record BinaryExpr(string Operator, SqlExpr Left, SqlExpr Right) : SqlExpr;

/// <summary><c>a AND b AND c</c> or <c>a OR b OR c</c>, flattened so a long chain is not a deep tree.</summary>
internal sealed record LogicalExpr(bool IsAnd, IReadOnlyList<SqlExpr> Terms) : SqlExpr;

internal sealed record NotExpr(SqlExpr Operand) : SqlExpr;

internal sealed record NegateExpr(SqlExpr Operand) : SqlExpr;

internal sealed record IsNullExpr(SqlExpr Operand, bool Negated) : SqlExpr;

internal sealed record InListExpr(SqlExpr Operand, IReadOnlyList<SqlExpr> Items, bool Negated) : SqlExpr;

/// <summary><c>x [NOT] IN (SELECT [alias.]col FROM [table] AS alias [WHERE ...])</c>, the one subquery shape YesSql emits.</summary>
internal sealed record InSubqueryExpr(SqlExpr Operand, SubSelect Query, bool Negated) : SqlExpr;

internal sealed record SubSelect(ColumnRef Column, string Table, string Alias, SqlExpr? Where);

internal sealed record ParenExpr(SqlExpr Inner) : SqlExpr;

/// <summary>A function call such as <c>MAX(x)</c> or <c>count(DISTINCT x)</c>; <see cref="Name"/> is as written.</summary>
internal sealed record FunctionExpr(string Name, IReadOnlyList<SqlExpr> Arguments, bool Distinct = false) : SqlExpr;

/// <summary><c>*</c> or <c>alias.*</c>, as a select item or the argument of <c>count(*)</c>.</summary>
internal sealed record StarExpr(string? Qualifier) : SqlExpr;

/// <summary>A <c>"double quoted"</c> name, as in the part of <c>DateTimePart("year", [Date])</c>.</summary>
internal sealed record QuotedNameExpr(string Name) : SqlExpr;

/// <summary>A bare word naming a select item alias, as in <c>ORDER BY order_1</c>.</summary>
internal sealed record AliasRefExpr(string Name) : SqlExpr;
