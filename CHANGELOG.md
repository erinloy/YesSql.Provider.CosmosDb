# Changelog

All notable changes are listed here. The project is in preview, so minor versions may change behavior.

## 0.1.3 (not yet released)

### Added
- `UPDATE [T] SET [Col] = REPLACE([Col], from, to) [WHERE ...]` is supported. Orchard Core issues this to rename serialized `$type` names in stored documents, for example in the Lucene query-type migration. It previously failed with `Parameter 'Id' not found`. The `from` and `to` arguments may be string literals or parameters, and the rewrite is recorded for rollback.

### Changed
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
