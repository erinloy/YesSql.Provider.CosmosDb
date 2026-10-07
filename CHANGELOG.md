# Changelog

All notable changes are listed here. The project is in preview, so minor versions may change behavior.

## Unreleased

### Fixed
- A query ordered by a date column on an index (`OrderBy(x => x.CreatedUtc)`) could return rows out of order. The provider sorts these in the client, and compared the dates as the culture's text, which drops the fractions of a second and sorts `9:59 AM` after `10:00 AM`, so items created within the same second tied and kept index order. Dates are now compared as moments in time. Found by running content operations through a real Orchard Core site.

### Added
- Orchard Core content checks (`SMOKE_CONTENT=1 scripts/smoke-sample.sh`): content items are created, published, edited as drafts, unpublished and removed through Orchard's content manager, queried in order and by page, created concurrently, and cancelled or failed requests are checked to leave nothing behind. CI runs them on Orchard Core 3.0.1 and the 4.0 preview with both partition strategies.

## 0.1.5

### Fixed
- Concurrent saves could fail with `Could not allocate a sequence id`. Each index row id was taken with a read and a conditional write on one counter document per table, and writers that shared a counter collided and retried 16 times. At the emulator's latency that was rare, but on a real account (about 60 ms per round trip) 16 concurrent sessions exhausted the retries. Ids are now reserved 32 at a time and handed out from memory, so most inserts never touch the counter. Ids remain unique across processes and across versions that share a database, but a restart leaves a gap of up to 31 unused ids. A save also makes about 25% fewer requests: 6 instead of 8 for a document with a map and a reduce index, measured on a real account.
- A `DateTimeOffset` with a UTC offset east of +01:00 could not be found by an equality or `<=` query on a real Cosmos DB account. Cosmos DB compares `DateTimeToTimestamp(c.x)` wrongly in a `WHERE` clause when the stored text carries such an offset (`...+05:30` never equals its own instant). The emulator does not reproduce this. The provider now stores and binds moments in time as UTC instants (`...Z`): a `DateTimeOffset` is written as UTC, and a `DateTime` of kind `Local` is converted to UTC. `DateTime` values of unspecified or UTC kind are unchanged. Rows written earlier with an east offset still cannot be found by equality on a real account; a range query finds them.

### Changed
- Queries over one map index cost fewer request units and round trips on a real account. A count, a first match, or a page ordered by document id (the order YesSql gives a paged query that has no order of its own) is now answered by Cosmos from the distinct document ids, where it used to read every matching index row to the client. Documents on a page are loaded with one query per 100 ids instead of one point read each. Measured on a real serverless account with 500 index rows per key: a count 18 RU to 7, a first match 19 RU to 4, and a page of 20 documents 38 RU and 21 requests to 9 RU and 2 requests. Orders on an index column, reduce and multi-index joins, and queries that filter on the document type still gather the matching rows in the client, so their cost grows with the rows per key.
- Statements are now parsed, not matched with regular expressions. Each statement is read once into a tree, and the provider runs it from the tree. Reading a statement takes about 3 microseconds, against about 1 millisecond for the regular expressions, and a statement is no longer at the mercy of a pattern that matches it wrongly.
- A statement the parser cannot read throws a `DbException` that says where. A malformed `renamecolumn` now does too, where it used to throw `NotSupportedException`. An `ORDER BY` term the provider cannot apply (a function, an unknown alias, a document column other than `Id` on an index query) throws instead of being dropped.
- An `UPDATE` with no `@Id`, a `REPLACE` whose parameter is missing or that sits next to another assignment, and an `INSERT` value that is neither a parameter nor a literal now throw instead of writing a partial or wrong row.
- Queries ordered by `OrderBy(x => x.Id)` on an index join are now ordered. The regexes dropped that term, so the rows came back in index order.
- Documents found through a reduce index are returned in a stable order: by the order the query gave on index columns, then by document id (descending only when the query asked for it). Before, ties came back in whatever order Cosmos returned the bridge rows, so a page of a tied result could repeat or skip a document.
- `TRUE` and `FALSE` are accepted as literals.

## 0.1.4

### Changed
- The README, the Orchard sample and the test helpers now skip TLS certificate validation only when the endpoint is a loopback address, which is how the Cosmos emulator is reached. Earlier versions of the README showed a client setup that accepted any server certificate; if you copied it for a real account, replace it with the snippet in the README.

### Fixed
- `ISession.CancelAsync()` did not undo writes that had already been sent to Cosmos. YesSql releases the transaction by disposing it, and the provider's transaction only rolled back when `Rollback` was called, so a unit of work that was flushed (by a query inside the session, for example) and then cancelled left its documents and index rows behind. YesSql does the same when one of its own reads or queries fails, and Orchard Core calls `CancelAsync` to discard a request's changes. Disposing an uncommitted transaction now rolls it back, as ADO.NET expects. Data written by earlier versions is unaffected, but documents or index rows left behind by an earlier cancelled unit of work are not cleaned up.

## 0.1.3

### Added
- Works with YesSql 6.0.0. The package is still built against YesSql 5.4.7, and YesSql 6's own test suite (`CoreTests`) passes against that build, on both partition strategies. CI now runs it.
- `OrderByRandom` and `ThenByRandom` now produce a random order. Cosmos cannot order by a function, so these queries are ordered and paged in the client. They previously returned rows in storage order.
- `CosmosDbException`, a `DbException` that carries the Cosmos status code and retry delay. Failed statements, failed connection opens and failed rollbacks throw it, with the original `CosmosException` as the inner exception. YesSql 6 requires failed queries to surface as `DbException`.
- `UPDATE [T] SET [Col] = REPLACE([Col], from, to) [WHERE ...]` is supported. Orchard Core issues this to rename serialized `$type` names in stored documents, for example in the Lucene query-type migration. It previously failed with `Parameter 'Id' not found`. The `from` and `to` arguments may be string literals or parameters, and the rewrite is recorded for rollback.

- The Orchard Core sample builds against Orchard Core 3.0.1 (YesSql 5.4.7) or the 4.0 preview (YesSql 6.0.0), selected with `-p:OrchardCoreVersion`. CI sets up and serves it on both. `scripts/smoke-sample.sh` runs the same check locally.

### Fixed
- Queries through a reduce index ignored `ORDER BY`, so documents came back in storage order. They are now ordered by the index row they belong to, and `Skip` and `Take` follow that order.
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
