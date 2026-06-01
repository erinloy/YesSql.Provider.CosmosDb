using System.Data;
using YesSql.Provider.CosmosDb.Internal;
using YesSql.Sql;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// YesSql SQL dialect for Cosmos DB. This emits a deliberately small, self-defined SQL surface that the
/// co-designed ADO.NET shim (<see cref="CosmosDbCommand"/>) parses and maps to Cosmos SDK operations.
/// We own both sides of this contract, so the dialect only ever produces statements the shim understands.
/// </summary>
public sealed class CosmosDbDialect : BaseDialect
{
    public CosmosDbDialect()
    {
        // 'now' maps to Cosmos server time at translation; placeholder template for parity.
        Methods.Add("now", new TemplateFunction("GetCurrentDateTime()"));
    }

    public override string Name => "CosmosDb";

    // Execute commands individually (no SQL batch) so the shim sees one well-known statement at a time.
    public override bool SupportsBatching => false;

    // Identity is produced by YesSql's IIdGenerator (block allocation), not by a DB identity column.
    // Cosmos has no auto-increment, so these DDL/identity fragments are unused by the shim.
    public override string IdentityColumnString => "";
    public override string LegacyIdentityColumnString => "";
    public override string IdentitySelectString => "";
    public override string IdentityLastId => "";

    public override string RandomOrderByClause => "GetCurrentTimestamp()";

    public override byte DefaultDecimalPrecision => 19;
    public override byte DefaultDecimalScale => 5;

    // Bracket quoting — easy and unambiguous for the shim's parser to strip.
    public override string QuoteForColumnName(string columnName) => "[" + columnName + "]";
    public override string QuoteForTableName(string tableName, string schema) => "[" + tableName + "]";
    public override string QuoteForAliasName(string aliasName) => aliasName;

    public override bool SupportsIfExistsBeforeTableName => true;

    public override string GetCreateSchemaString(string schema) => null!;

    // DDL is a no-op on a schemaless store; containers are provisioned by the connection.
    public override string GetDropIndexString(string indexName, string tableName, string schema) => "";

    public override string GetTypeName(DbType dbType, int? length, byte? precision, byte? scale)
        // Cosmos is schemaless; column types are irrelevant since DDL is not executed.
        => "TEXT";

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
