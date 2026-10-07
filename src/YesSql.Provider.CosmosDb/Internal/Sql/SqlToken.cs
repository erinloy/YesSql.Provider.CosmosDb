using System;

namespace YesSql.Provider.CosmosDb.Internal.Sql;

internal enum SqlTokenKind
{
    /// <summary>A bare word: a keyword, a function name or an unquoted identifier.</summary>
    Word,

    /// <summary>A <c>[bracketed]</c> identifier. <see cref="SqlToken.Value"/> is the name without the brackets.</summary>
    Bracketed,

    /// <summary>A parameter such as <c>@p0</c>. <see cref="SqlToken.Value"/> is the name without the <c>@</c>.</summary>
    Parameter,

    /// <summary>An unsigned integer or decimal number.</summary>
    Number,

    /// <summary>A <c>'single quoted'</c> literal. <see cref="SqlToken.Value"/> is the unescaped content.</summary>
    String,

    /// <summary>A <c>"double quoted"</c> literal, as used for the part name in <c>DateTimePart("year", ...)</c>.</summary>
    Quoted,

    /// <summary>An operator or punctuation: <c>( ) , . ; = &lt; &gt; &lt;&gt; &lt;= &gt;= != || * + - / %</c>.</summary>
    Symbol,
}

/// <summary>One token of a statement. <see cref="Text"/> is the exact source slice at <see cref="Position"/>.</summary>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, string Value, int Position)
{
    public int End => Position + Text.Length;

    public bool IsWord(string keyword)
        => Kind == SqlTokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string symbol)
        => Kind == SqlTokenKind.Symbol && string.Equals(Text, symbol, StringComparison.Ordinal);

    public override string ToString() => $"{Kind} '{Text}' at {Position}";
}
