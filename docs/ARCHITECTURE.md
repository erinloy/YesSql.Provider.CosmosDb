# Architecture

## Why the provider looks like this

YesSql has no storage abstraction below the session. It persists through an ADO.NET `DbConnection` returned by `IConfiguration.ConnectionFactory`, and its commands build SQL text with an `ISqlDialect` and run it through Dapper. The extension points are `ConnectionFactory`, `SqlDialect` and `CommandInterpreter`.

So the provider is two matched pieces:

1. A small ADO.NET implementation (`DbConnection`, `DbCommand`, `DbDataReader`, `DbTransaction`) backed by the Cosmos SDK.
2. `CosmosDbDialect`, which emits a restricted SQL surface that the command class understands.

Because the dialect and the command class are written together, the command class does not parse arbitrary SQL. It recognizes the statement shapes that YesSql's command templates and `CosmosDbDialect` produce, plus the few extra shapes Orchard Core issues directly (see [Statement handling](#statement-handling)).

## Storage model

Everything lives in one container. Each item carries the Cosmos system `id`, a partition key in `pk`, and a `__table` field naming the YesSql table it came from.

| YesSql concept | Cosmos item |
| --- | --- |
| Document row | `{ id: "<table>:<Id>", pk, __table, Id, Type, Content, Version }` |
| Map index row | `{ id: "<table>:<Id>", pk, __table, Id, <index columns>, DocumentId }` |
| Reduce index row | Same shape as a map index row. |
| Reduce bridge row | `{ id: "<bridge table>:<index id>:<document id>", pk, __table, <columns> }` |
| Id counter | `{ id: "<table>", pk: "__seq", next }` |

`pk` is the table name with `PerTable` and `CosmosDbOptions.PartitionScope` with `PerStore`. `pk` is the default name of the partition key property; `CosmosDbOptions.PartitionKeyPath` can name another single-level path. See [PARTITIONING.md](PARTITIONING.md).

`Id` is a numeric field separate from the system `id` string. Cosmos has no auto-increment, so ids for index rows come from the counter item for that table. The counter lives in its own `__seq` partition, so it never appears in queries over a table, and it is advanced with an ETag-conditional replace. Ids are not reused after deletes.

Cosmos indexes every property by default, so YesSql's index tables need no DDL. `CosmosDbCommandInterpreter` turns schema commands into no-ops, with one exception: `RenameColumn` rewrites the field on every item of that table.

## Request flow

```
YesSql session
  -> command.ExecuteAsync(DbConnection, ...)     SQL text built with CosmosDbDialect
     -> Dapper
        -> CosmosDbCommand.Execute*Async         SQL text -> Cosmos operations
           -> Microsoft.Azure.Cosmos SDK
```

`CosmosDbDialect.SupportsBatching` is `false`, so YesSql sends one statement at a time.

### Statement handling

`CosmosDbCommand` dispatches on the statement text.

**Writes (`ExecuteNonQuery`)**

- `INSERT` and `UPDATE` on document and index tables: read the existing item if any, set the provided columns, and upsert. An `UPDATE` carrying a `[Version]` condition is a checked update: it compares versions and then does a replace conditioned on the item's ETag. A mismatch returns 0 affected rows, which YesSql turns into a `ConcurrencyException`.
- `INSERT` into a reduce bridge table: the columns are mapped to parameters by position and the item id is built from the index id and document id.
- `DELETE`: query the table's items that match the `WHERE` clause, then delete each one. This covers document deletes, deletes of index rows by `DocumentId`, and bridge row deletes.
- `UPDATE [T] SET [C] = REPLACE([C], from, to) [WHERE ...]`: query the matching items and rewrite the column in each. Orchard Core issues this to rename serialized type names in stored documents.
- `renamecolumn`: rewrite a field across a table, as described above.

**Scalars (`ExecuteScalar`)**

- `SELECT MAX([Id])`, used to seed YesSql's id generator.
- `INSERT` into an index table, returning the new row's id from the counter.
- `COUNT(...)`, with or without an index join.

**Reads (`ExecuteReader`)**

- Documents by id (`WHERE [Id] = / IN`): point reads.
- Documents by type, or a document query without an index: a query over the table's items, optionally filtered on `Type`, with the requested columns projected.
- Index rows (`SELECT ... FROM [index]`): a query over the index's items. Column names come from the properties of the returned items.
- Index joins (`... JOIN [index] AS a ON a.[DocumentId] = [Document].[Id]`), including joins on several different indexes and the map-plus-reduce form: a query against each index partition collects the matching `DocumentId` values, which are intersected when there are several indexes. Ordering and paging are applied to that id list in the client, then only the documents on the requested page are point-read, up to eight reads at a time.
- Reduce index queries (document, bridge and index tables): resolved through the index, then the bridge rows, then the documents.
- `SELECT DateTimePart(...)` projections: run as a Cosmos `VALUE` query.

### Predicate, ordering and paging translation

- Column references (`alias.[Col]`, `[table].[Col]`, `[Col]`) are rewritten to `c["Col"]`. `IS NULL` and `IS NOT NULL` map to `IS_DEFINED` and `IS_NULL` tests.
- The `[Document].[Type] = @p` predicate that YesSql adds to index joins is removed from the index query, because `Type` is not an index column. For `filterType` queries it is applied afterwards against the collected documents.
- Comparisons against `DateTime` and `DateTimeOffset` parameters use `DateTimeToTimestamp`, so values compare by instant regardless of offset text.
- `IN (SELECT ...)` subqueries are run first and replaced with an `ARRAY_CONTAINS` test over the resulting values, which are passed as a query parameter. Cosmos has no correlated subqueries across partitions.
- Document and index-row queries map YesSql's `ORDER BY` (including the `MAX(a.[Col]) AS order_N` form) to a Cosmos `ORDER BY` and push `OFFSET`/`LIMIT` into the query. A lone `OFFSET` is paired with a maximum `LIMIT`, because Cosmos requires both.
- Index joins sort in the client. Cosmos `ORDER BY` is case-sensitive and cannot order by `LOWER(...)`, and the reference providers order case-insensitively.
- `byte[]` column values are stored as `{ "$b64": "<base64>" }` so they round-trip as `byte[]`.

## Transactions

Writes are applied when the statement runs, so that a later read in the same session (YesSql's autoflush) sees them. Each write records its inverse in the transaction's undo log: delete for a created item, restore of the prior snapshot for a changed or deleted one. `Commit` discards the log. `Rollback` replays it in reverse. See [PARTITIONING.md](PARTITIONING.md) for what that guarantees under each strategy.

## Connections

`CosmosDbConnection` shares one `CosmosClient` per account endpoint and key for the life of the process, as the Cosmos SDK recommends. It creates the database and container once per process and endpoint, not on every connection open.

## Source map

| File | Role |
| --- | --- |
| `CosmosDbProviderOptionsExtensions.cs` | `UseCosmosDb(...)` entry point |
| `CosmosDbOptions.cs`, `PartitionStrategy.cs` | Configuration |
| `CosmosDbDialect.cs` | `ISqlDialect`: quoting, type names, paging, date functions |
| `CosmosDbCommandInterpreter.cs` | Schema commands (no-ops except `RenameColumn`) |
| `Internal/CosmosDbConnectionFactory.cs` | `IConnectionFactory` carrying the options |
| `Internal/CosmosDbConnection.cs` | `DbConnection`; shared client and one-time provisioning |
| `Internal/CosmosDbCommand.cs` | SQL-to-Cosmos translation |
| `Internal/CosmosDbTransaction.cs` | Undo log and rollback |
| `Internal/CosmosDbDataReader.cs` | In-memory forward-only reader |
| `Internal/CosmosDbParameter*.cs` | Parameter types |

## Related work

The single-container, type-discriminated layout is adapted from [Hangfire.AzureCosmosDb](https://github.com/imranmomin/Hangfire.AzureCosmosDb).
