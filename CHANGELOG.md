# Changelog

All notable changes are listed here. The project is in preview, so minor versions may change behavior.

## 0.1.3 (not yet released)

### Added
- Works with YesSql 6.0.0. The package is still built against YesSql 5.4.7, and YesSql 6's own test suite (`CoreTests`) passes against that build, on both partition strategies. CI now runs it.
- `OrderByRandom` and `ThenByRandom` now produce a random order. Cosmos cannot order by a function, so these queries are ordered and paged in the client. They previously returned rows in storage order.
- `CosmosDbException`, a `DbException` that carries the Cosmos status code and retry delay. Failed statements, failed connection opens and failed rollbacks throw it, with the original `CosmosException` as the inner exception. YesSql 6 requires failed queries to surface as `DbException`.
- `UPDATE [T] SET [Col] = REPLACE([Col], from, to) [WHERE ...]` is supported. Orchard Core issues this to rename serialized `$type` names in stored documents, for example in the Lucene query-type migration. It previously failed with `Parameter 'Id' not found`. The `from` and `to` arguments may be string literals or parameters, and the rewrite is recorded for rollback.

### Fixed
- Queries with `IN (SELECT ...)` failed, or could be changed by the data, when a stored value contained a backslash or a quote. The values are now passed to Cosmos as a query parameter instead of being written into the query text.
- A failed attempt to create the database or container was remembered, so every later connection in the process failed the same way. It is now retried on the next connection.
- A `PartitionKeyPath` other than `/pk` was accepted but did not work. Single-level paths such as `/tenantId` now work, other paths are rejected when the provider is configured, and the container's actual path is checked when the first connection opens.
- `renamecolumn` statements that cannot be parsed, and `REPLACE` with a `NULL` search value, now throw `NotSupportedException` instead of silently doing nothing. A `REPLACE` with an empty search value changes nothing, as in SQL.
- Cosmos errors from index queries were rewrapped as `NotSupportedException`, which hid throttling and availability errors. They now surface as `CosmosDbException`.
- With `PerStore`, rollback sends one operation per item touched rather than one per write, so more units of work fit in a single transactional batch.
- Retries when two writers allocate index ids for the same table at once now back off and allow more attempts.

### Changed
- A page of documents loaded through an index is read with up to eight concurrent point reads instead of one at a time.
- Removing duplicate document ids is linear instead of quadratic, and the reduce-index bridge lookup passes its id list as a parameter.
- The package depends on `YesSql.Core` instead of the `YesSql` meta-package, so it no longer brings the SQL Server, PostgreSQL, MySQL and SQLite drivers into your dependency graph. Applications that reference `YesSql` are not affected.
- The classes in the `YesSql.Provider.CosmosDb.Internal` namespace are now `internal`. They were public by accident and are not meant to be used directly. Configure the provider through `UseCosmosDb`.

## 0.1.2

### Fixed
- A `Skip` without a `Take` (a bare `OFFSET`) was dropped. Cosmos rejects `OFFSET` without `LIMIT`, so the provider now pairs a lone offset with a maximum `LIMIT`. This restored the full YesSql conformance suite to 249 of 249 on both partition strategies.

## 0.1.1

Fixes found by running a full Orchard Core setup against the classic (non-PostgreSQL) emulator.

### Fixed
- One `CosmosClient` is now shared per account instead of one per connection. A client per connection exhausted sockets and operations hung under load.
- `OFFSET` and `LIMIT` are pushed into the Cosmos query instead of trimming results in the client.
- The database and container are created once per process instead of on every connection open. These are control-plane operations that are rate limited on real accounts.

## 0.1.0

Initial preview.

- Documents, map indexes, reduce indexes, queries, optimistic concurrency and unit-of-work rollback.
- `PerTable` and `PerStore` partition strategies.
- About 92% of YesSql's conformance suite passed. Orchard Core could start and run on the provider.
