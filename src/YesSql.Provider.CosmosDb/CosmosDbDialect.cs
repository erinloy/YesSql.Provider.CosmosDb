using System;
using System.Collections.Generic;
using System.Data;
using YesSql.Sql;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// YesSql SQL dialect for Cosmos DB. YesSql builds its SQL with this dialect, and the provider's ADO.NET command parses that
/// SQL and runs it as Cosmos operations. The dialect quotes names with brackets, which the parser reads, and returns the
/// fragments YesSql asks for. Those that belong to DDL or to identity columns are never run, because Cosmos has neither.
/// </summary>
public sealed class CosmosDbDialect : BaseDialect
{
    static CosmosDbDialect()
    {
        _propertyTypes = new Dictionary<Type, DbType>
        {
            { typeof(object), DbType.Binary },
            { typeof(byte[]), DbType.Binary },
            { typeof(string), DbType.String },
            { typeof(char), DbType.StringFixedLength },
            { typeof(bool), DbType.Boolean },
            { typeof(byte), DbType.Byte },
            { typeof(sbyte), DbType.SByte },
            { typeof(short), DbType.Int16 },
            { typeof(ushort), DbType.UInt16 },
            { typeof(int), DbType.Int32 },
            { typeof(uint), DbType.UInt32 },
            { typeof(long), DbType.Int64 },
            { typeof(ulong), DbType.UInt64 },
            { typeof(float), DbType.Single },
            { typeof(double), DbType.Double },
            { typeof(decimal), DbType.Decimal },
            { typeof(DateTime), DbType.DateTime },
            { typeof(DateTimeOffset), DbType.DateTimeOffset },
            { typeof(Guid), DbType.Guid },
            { typeof(TimeSpan), DbType.Time },
            { typeof(char?), DbType.StringFixedLength },
            { typeof(bool?), DbType.Boolean },
            { typeof(byte?), DbType.Byte },
            { typeof(sbyte?), DbType.SByte },
            { typeof(short?), DbType.Int16 },
            { typeof(ushort?), DbType.UInt16 },
            { typeof(int?), DbType.Int32 },
            { typeof(uint?), DbType.UInt32 },
            { typeof(long?), DbType.Int64 },
            { typeof(ulong?), DbType.UInt64 },
            { typeof(float?), DbType.Single },
            { typeof(double?), DbType.Double },
            { typeof(decimal?), DbType.Decimal },
            { typeof(DateTime?), DbType.DateTime },
            { typeof(DateTimeOffset?), DbType.DateTimeOffset },
            { typeof(Guid?), DbType.Guid },
            { typeof(TimeSpan?), DbType.Time },
        };
    }

    /// <summary>Creates the dialect and registers its SQL function templates.</summary>
    public CosmosDbDialect()
    {
        // Template for YesSql's 'now' function.
        Methods.Add("now", new TemplateFunction("GetCurrentDateTime()"));

        // Date-part extraction, as Cosmos DateTimePart(part, date). The command recognizes a
        // DateTimePart(...) projection and runs it as a scalar Cosmos query.
        Methods.Add("second", new TemplateFunction("DateTimePart(\"second\", {0})"));
        Methods.Add("minute", new TemplateFunction("DateTimePart(\"minute\", {0})"));
        Methods.Add("hour", new TemplateFunction("DateTimePart(\"hour\", {0})"));
        Methods.Add("day", new TemplateFunction("DateTimePart(\"day\", {0})"));
        Methods.Add("month", new TemplateFunction("DateTimePart(\"month\", {0})"));
        Methods.Add("year", new TemplateFunction("DateTimePart(\"year\", {0})"));
    }

    /// <inheritdoc />
    public override string Name => "CosmosDb";

    // Execute commands individually (no SQL batch) so the shim sees one well-known statement at a time.
    /// <inheritdoc />
    public override bool SupportsBatching => false;

    // Document ids come from YesSql's IIdGenerator, not from an identity column, and Cosmos has none. The identity column
    // fragments are DDL, which is never run. IdentitySelectString is different: YesSql appends it to the insert of an index
    // row to read the new id back, so it is how the command knows that the insert wants an id.
    /// <inheritdoc />
    public override string IdentityColumnString => "";
    /// <inheritdoc />
    public override string LegacyIdentityColumnString => "";
    /// <inheritdoc />
    public override string IdentitySelectString => "RETURNING";
    /// <inheritdoc />
    public override string IdentityLastId => "";

    // Cosmos cannot order by a function, so the provider recognises this one in a parsed statement and orders the rows
    // itself. The clause is written here only so that YesSql has something to emit.
    internal const string RandomFunction = "GetCurrentTimestamp";

    /// <inheritdoc />
    public override string RandomOrderByClause => RandomFunction + "()";

    /// <inheritdoc />
    public override byte DefaultDecimalPrecision => 19;
    /// <inheritdoc />
    public override byte DefaultDecimalScale => 5;

    // Bracket quoting, which the SQL lexer reads unambiguously.
    /// <inheritdoc />
    public override string QuoteForColumnName(string columnName) => "[" + columnName + "]";
    /// <inheritdoc />
    public override string QuoteForTableName(string tableName, string schema) => "[" + tableName + "]";
    /// <inheritdoc />
    public override string QuoteForAliasName(string aliasName) => aliasName;

    /// <inheritdoc />
    public override bool SupportsIfExistsBeforeTableName => true;

    /// <inheritdoc />
    public override string GetCreateSchemaString(string schema) => null!;

    // DDL is a no-op on a schemaless store; containers are provisioned by the connection.
    /// <inheritdoc />
    public override string GetDropIndexString(string indexName, string tableName, string schema) => "";

    /// <inheritdoc />
    public override string GetTypeName(DbType dbType, int? length, byte? precision, byte? scale)
        // Cosmos is schemaless; column types are irrelevant since DDL is not executed. Decimal still
        // reports a precision/scale-qualified name so callers that inspect the type definition (and the
        // conformance suite) see the expected DECIMAL(p,s) form rather than a bare type.
        => dbType == DbType.Decimal
            ? $"DECIMAL({precision ?? DefaultDecimalPrecision},{scale ?? DefaultDecimalScale})"
            : "TEXT";

    /// <inheritdoc />
    public override void Page(ISqlBuilder sqlBuilder, string offset, string limit)
    {
        sqlBuilder.ClearTrail();

        if (limit != null)
        {
            sqlBuilder.Trail(" LIMIT ");
            sqlBuilder.Trail(limit);
        }

        if (offset != null)
        {
            sqlBuilder.Trail(" OFFSET ");
            sqlBuilder.Trail(offset);
        }
    }
}
