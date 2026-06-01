# Architecture

## The constraint that shapes everything

YesSql has **no pluggable document-store seam**. It persists exclusively through an ADO.NET
`DbConnection` obtained from `IConfiguration.ConnectionFactory`, driven by SQL strings that its command
classes build via `ISqlDialect`, executed with Dapper. Verified from the YesSql v5.4.7 source:

- `IConnectionFactory.CreateConnection()` returns a `System.Data.Common.DbConnection`.
- `Session.FlushAsync` runs typed `IIndexCommand`s, each of which calls
  `command.ExecuteAsync(DbConnection, DbTransaction, ISqlDialect, …)` and emits SQL inline.
- `ISqlDialect` is a ~50-member SQL-string builder.

Because we ship a **NuGet package, not a fork**, the only way in is YesSql's public extension points:
`ConnectionFactory`, `SqlDialect`, `CommandInterpreter`. So the provider is a **co-designed pair**:

1. a **Cosmos-backed ADO.NET shim** registered via a custom `IConnectionFactory`, and
2. a **`CosmosDbDialect`** that emits a small, self-defined SQL surface the shim recognizes.

We own both sides of that SQL contract, so the shim never parses arbitrary SQL — only the bounded set
of statement shapes our own dialect and YesSql's (fixed) command templates produce.

## Storage model

A single Cosmos **container** holds everything, type-discriminated, partitioned by the source SQL
table name (`pk`):

| YesSql concept | Cosmos item |
| --- | --- |
| Document (table `tpDocument`, `tpCol1_Document`, …) | `{ id: "<table>:<Id>", pk: "<table>", Id, Type, Content, Version }` |
| Map-index row (table `tpPersonByName`, …) | `{ id: "<table>:<genId>", pk: "<table>", Id, <indexcols…>, DocumentId }` |
| Reduce bridge row *(planned)* | `{ id: "<bridge>:<indexId>:<docId>", pk: "<bridge>", <IndexName>Id, DocumentId }` |

- `Id` is a numeric field (distinct from Cosmos's system `id` string). Cosmos has no auto-increment, so
  index-row `Id`s are allocated as `MAX(c.Id) + 1` within the partition.
- Cosmos automatically indexes every property, so YesSql's separate index *tables* become queryable
  document properties — no DDL is needed (the `CommandInterpreter` is a no-op).

## Request flow

```
YesSql Session
  └─ command.ExecuteAsync(DbConnection, …)         // SQL string built via CosmosDbDialect
       └─ Dapper → CosmosDbCommand.Execute*Async    // the translator
            └─ Microsoft.Azure.Cosmos SDK ops on the container
```

`CosmosDbCommand` dispatches on the statement shape:

- **Writes** (`ExecuteNonQuery`): `insert`/`update` → `UpsertItem`; `delete` → query items matching the
  WHERE, then delete each (handles document delete, map-index delete by `[DocumentId]`, etc.).
- **Scalars** (`ExecuteScalar`): `SELECT MAX([Id])` (id-gen seed), map-index `insert` (returns a
  generated `Id`), and `COUNT(*)` over a partition or an index join.
- **Reads** (`ExecuteReader`):
  - `SELECT … WHERE [Id] = / IN` → point-reads by id.
  - `SELECT [Document].* … WHERE [Type] = @Type` (and the `ListAsync` subquery-dedup form) → query the
    document partition, optionally filtered by `Type`.
  - `SELECT * FROM [index] …` → return index rows with dynamic columns.
  - Index join (`… JOIN [index] AS a ON a.[DocumentId] = [Document].[Id] WHERE …`) → **two-step**:
    query the index partition for matching `DocumentId`s, then point-read those documents.

### WHERE / ORDER BY / paging translation

- `TranslateWhere` rewrites column refs `alias.[Col]` / `[table].[Col]` / `[Col]` → `c["Col"]` in a
  single pass, and maps `IS [NOT] NULL` → Cosmos `IS_NULL` / `IS_DEFINED`.
- `StripDocTypePredicate` removes the `[Document].[Type] = @p` predicate YesSql adds to index joins
  (it doesn't apply inside the index partition).
- `BuildOrderClause` maps YesSql's `MAX(a.[Col]) AS order_N … ORDER BY order_N [DESC]` aggregate form
  to Cosmos `ORDER BY c["Col"] [DESC]`.
- `OFFSET` / `LIMIT` are applied as `Skip`/`Take` after ordering.
- `IN (SELECT … FROM [index] …)` subqueries are resolved by **pre-executing the inner query** into a
  literal `IN (…)` list (Cosmos has no cross-partition correlated subqueries).

`SupportsBatching` is **false** so YesSql sends one statement at a time (no multi-statement SQL batch
to parse).

## File map

| File | Role |
| --- | --- |
| `CosmosDbProviderOptionsExtensions.cs` | `UseCosmosDb(IConfiguration, CosmosDbOptions)` entry point |
| `CosmosDbOptions.cs` | endpoint/key/database/container/partition + emulator `ClientOptions` |
| `CosmosDbDialect.cs` | `ISqlDialect` — quoting, type map, `SupportsBatching=false`, paging |
| `CosmosDbCommandInterpreter.cs` | no-op DDL (Cosmos is schemaless) |
| `Internal/CosmosDbConnection.cs` | `DbConnection` shim; provisions database/container on open |
| `Internal/CosmosDbConnectionFactory.cs` | `IConnectionFactory` carrying `CosmosDbOptions` |
| `Internal/CosmosDbCommand.cs` | the SQL→Cosmos translator (the heart of the provider) |
| `Internal/CosmosDbDataReader.cs` | forward-only reader over an in-memory result set |
| `Internal/CosmosDbParameter*.cs`, `CosmosDbTransaction.cs` | ADO.NET shim plumbing |

## Prior art

Storage-model and lock patterns are adapted from
[`imranmomin/Hangfire.AzureCosmosDb`](https://github.com/imranmomin/Hangfire.AzureCosmosDb)
(single container + type discriminator, partition-scoped stored procedures for atomicity,
TTL + ETag distributed lock). Those stored-procedure/lock patterns are the intended basis for the
transaction work (see `CONFORMANCE.md`).
