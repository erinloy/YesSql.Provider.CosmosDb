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

`Id` is a numeric field separate from the system `id` string. Cosmos has no auto-increment, so ids for index rows come from the counter item for that table. The counter lives in its own `__seq` partition, so it never appears in queries over a table. It holds the highest id reserved. A process reserves ids 32 at a time with an ETag-conditional replace and hands them out from memory (`SequenceBlocks`), so most inserts make no request for their id and concurrent inserts rarely contend on the counter. Ids stay unique across processes and are not reused after deletes. A restart leaves a gap of up to 31 unused ids. Before blocks, every insert took the counter's ETag and sixteen concurrent writers on a real account (about 60 ms per round trip) exhausted the retries.

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

Each statement is parsed once when it runs, and `CosmosDbCommand` dispatches on the parse tree.

The parser is in `Internal/Sql`: a lexer (`SqlLexer`, one pass, `[bracketed]` names, `'strings'` and `@parameters`), a recursive-descent parser (`SqlParser`) that builds a statement tree (`SelectStatement`, `InsertStatement`, `UpdateStatement`, `DeleteStatement`, `RenameColumnStatement`) and expression trees, and `SelectShape`, which reads a parsed `SELECT` to find the document table, the index joins (map, several maps, reduce with its bridge table), the predicate, the ordering and the paging, wherever YesSql put them, including inside the derived table of an index query. The grammar is the subset YesSql and Orchard Core emit: joins, `WHERE` with `AND`, `OR`, `NOT`, comparisons, `LIKE`, `IN` lists, `IN (SELECT ...)`, `IS [NOT] NULL` and `||`, `GROUP BY`, `ORDER BY`, `OFFSET`, `LIMIT`, `count`, `MAX`, `REPLACE` and `DateTimePart`. Nesting is limited to 200 levels. A statement outside the grammar throws `SqlSyntaxException`, a `DbException`, and an `ORDER BY` term the provider cannot apply is refused rather than dropped. Parsing a statement takes a few microseconds.

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

- Documents by id (`WHERE [Id] = / IN`): a point read for one id, and one `ARRAY_CONTAINS` query per 100 ids for several, returned in the order asked with missing ids skipped.
- Documents by type, or a document query without an index: a query over the table's items, optionally filtered on `Type`, with the requested columns projected.
- Index rows (`SELECT ... FROM [index]`): a query over the index's items. Column names come from the properties of the returned items.
- Index joins (`... JOIN [index] AS a ON a.[DocumentId] = [Document].[Id]`), including joins on several different indexes and the map-plus-reduce form: a query against each index partition collects the matching `DocumentId` values, which are intersected when there are several indexes. Ordering and paging are applied to that id list in the client, then only the documents on the requested page are loaded, with one query per 100 ids. When the query is over a single index, is ordered by document id or not at all, and does not filter on the document type, Cosmos does the work instead: `SELECT VALUE COUNT(1) FROM (SELECT DISTINCT ...)` for a count and `SELECT DISTINCT ... ORDER BY DocumentId OFFSET n LIMIT m` for a page, so the cost no longer grows with the number of matching index rows.
- Reduce index queries (document, bridge and index tables): resolved through the index, then the bridge rows, then the documents.
- `SELECT DateTimePart(...)` projections: run as a Cosmos `VALUE` query.

### Predicate, ordering and paging translation

- Column references (`alias.[Col]`, `[table].[Col]`, `[Col]`) are rewritten to `c["Col"]`. `IS NULL` and `IS NOT NULL` map to `IS_DEFINED` and `IS_NULL` tests.
- The `[Document].[Type] = @p` predicate that YesSql adds to index joins is removed from the index query, because `Type` is not an index column. For `filterType` queries it is applied afterwards against the collected documents.
- Comparisons against `DateTime` and `DateTimeOffset` parameters use `DateTimeToTimestamp`, so values compare by instant regardless of offset text.
- `IN (SELECT ...)` subqueries are run first and replaced with an `ARRAY_CONTAINS` test over the resulting values, which are passed as a query parameter. Cosmos has no correlated subqueries across partitions.
- Document and index-row queries map YesSql's `ORDER BY` (including the `MAX(a.[Col]) AS order_N` form) to a Cosmos `ORDER BY` and push `OFFSET`/`LIMIT` into the query. A lone `OFFSET` is paired with a maximum `LIMIT`, because Cosmos requires both.
- Dates are stored as UTC instants (`...Z`). A `DateTimeOffset` is written as UTC and a `Local` `DateTime` is converted, because Cosmos DB compares `DateTimeToTimestamp(c.x)` wrongly in a `WHERE` clause for a stored offset east of +01:00, which the emulator does not reproduce.
- Index joins ordered by an index column sort in the client. Cosmos `ORDER BY` is case-sensitive and cannot order by `LOWER(...)`, and the reference providers order case-insensitively.
- `byte[]` column values are stored as `{ "$b64": "<base64>" }` so they round-trip as `byte[]`.

## Transactions

Writes are applied when the statement runs, so that a later read in the same session (YesSql's autoflush) sees them. Each write records its inverse in the transaction's undo log: delete for a created item, restore of the prior snapshot for a changed or deleted one. `Commit` discards the log. `Rollback` replays it in reverse. See [PARTITIONING.md](PARTITIONING.md) for what that guarantees under each strategy.

A write is started when the statement runs and the statement returns without waiting for Cosmos to answer (`CosmosDbTransaction.UpsertAsync` and `DeleteAsync`), so the writes of one save, the document and its index rows, are on the wire together. At most 8 are in flight. What has to wait:

- A query (any `SELECT`, `DELETE`, `UPDATE ... REPLACE` or `renamecolumn`) waits for every write in flight, so it sees them.
- An `UPDATE` of one item, which reads that item by id, waits only for an earlier write to the same item. A second write to an item also waits for the first, so two writes of one item reach Cosmos in the order they were issued.
- A checked `UPDATE` (one with a `[Version]` condition) is awaited, because its result, one row or none, is the answer YesSql needs.
- `Commit` and `Rollback` wait for everything in flight. Rollback has to, because it can only undo a write that has landed.

A write that fails is not lost. The failure is kept, and the next statement that waits for the writes in flight, or `Commit`, throws it (a `CosmosDbException` for a Cosmos error). YesSql then cancels the unit of work, which rolls back every write, including the ones that succeeded. The statement that issued the failed write has already returned, so the exception surfaces on a later statement or on `SaveChangesAsync`.

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
| `Internal/CosmosDbCommand.cs` | Statement dispatch and the read paths |
| `Internal/CosmosDbCommand.Writes.cs` | `INSERT`, `UPDATE`, `DELETE` and `renamecolumn` |
| `Internal/CosmosDbCommand.Where.cs` | `WHERE` clauses to Cosmos predicates |
| `Internal/Sql/` | Lexer, parser, statement and expression trees, `SelectShape`, and the writer that turns an expression into Cosmos text |
| `Internal/CosmosDbTransaction.cs` | Undo log and rollback |
| `Internal/CosmosDbDataReader.cs` | In-memory forward-only reader |
| `Internal/CosmosDbParameter*.cs` | Parameter types |

## Related work

The single-container, type-discriminated layout is adapted from [Hangfire.AzureCosmosDb](https://github.com/imranmomin/Hangfire.AzureCosmosDb).
