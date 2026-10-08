# Partitioning and transactions

## The constraint

Cosmos DB commits atomically only within one logical partition. There is no transaction across partitions. The two mechanisms are:

| Mechanism | Scope | Limits |
| --- | --- | --- |
| Transactional batch | One logical partition in one container | 100 operations, 2 MB, 5 seconds |
| Stored procedure | One logical partition | 5 seconds and the request unit budget |

A logical partition holds at most 20 GB and serves at most 10,000 RU/s.

A YesSql unit of work (one `SaveChangesAsync`) can write a document, several map and reduce index rows, and bridge rows. If those items sit in different partitions, no single Cosmos operation can commit or undo them together. This is why the provider offers two partition strategies.

## Strategies

Set `CosmosDbOptions.PartitionStrategy`.

### `PerTable` (default)

The partition key is the YesSql table name, so `Document`, `UserIndex` and so on are separate logical partitions. Queries over one table stay within one partition, and no single partition has to hold the whole store.

The ceiling moves to the table, though: every document of a collection is in the one `Document` partition, so the documents of a collection are limited to 20 GB and 10,000 RU/s, and every document write goes to that partition. Index tables are separate partitions, each with its own limits. This is a wide ceiling for a content store, and the wrong one for a store whose documents keep growing without bound.

A unit of work spans partitions, so it cannot be rolled back atomically. See [Rollback](#rollback).

### `PerStore`

Every item gets the same partition key, `CosmosDbOptions.PartitionScope`, and a `__table` field tells the tables apart. A unit of work stays inside one logical partition, so its rollback can be a single transactional batch.

The cost is the per-partition ceiling of 20 GB and 10,000 RU/s for the whole store. For Orchard Core that is the metadata for one tenant, since media normally lives in blob storage. Use a different `PartitionScope` per tenant (for example the shell name) if several tenants share a container.

## Rollback

Writes are applied immediately, so YesSql's autoflush (a query inside a session that sees the session's own unsaved changes) keeps working. A write is started without waiting for Cosmos to answer; the next query, `Commit` or `Rollback` waits for the writes in flight, and a write that failed is thrown there (see [ARCHITECTURE.md](ARCHITECTURE.md#transactions)). Each write records its inverse in an undo log held by the transaction. `Commit` discards the log. Disposing a transaction that was not committed, which is what `ISession.CancelAsync` and YesSql's own failure handling do, rolls it back. `Rollback` restores each item touched by the unit of work to the state recorded first, so it issues one operation per item however many times the item was written.

With `PerStore`, the inverse operations are sent as transactional batches in the single partition. A batch is all-or-nothing. Batches are limited to 100 operations, so a unit of work that touched more than 100 items is rolled back in several batches, and atomicity holds per batch rather than for the whole set. If the service rejects a batch, for example because an item to delete is already gone, the provider applies that batch's operations one at a time, which is not atomic. A rollback is therefore atomic when it fits in one batch and the batch is accepted.

With `PerTable`, the inverse operations are applied one at a time across partitions. If the process stops partway through a rollback, some writes are undone and some are not.

### What this does not provide

- Because writes are applied when issued, other sessions can read a unit of work's changes before it commits.
- The undo log restores a snapshot taken before the write. If another session changed the same item in between, rollback overwrites that change.
- Commit only waits for the writes still in flight; the data is already written. A crash before commit leaves the writes in place and the undo log lost.
- A write that Cosmos rejects is reported by a later statement or by the commit, not by the statement that issued it.

Optimistic concurrency is separate from rollback. A checked `UPDATE` compares the stored `Version` and uses the item's ETag, so concurrent writers to the same document get a `ConcurrencyException` instead of silently overwriting each other.

## Choosing

| If | Use |
| --- | --- |
| Data per store is bounded and you want failed requests to be reverted as completely as Cosmos allows (typical for an Orchard Core tenant) | `PerStore` |
| Documents of one collection may exceed 20 GB or 10,000 RU/s | Neither. Both put them in one logical partition. |
| Best-effort cleanup on failure is acceptable | `PerTable` |

Hierarchical partition keys raise the first-level size limit, but atomicity applies to the full key path, so they do not give atomic units of work across tables. The provider does not use them.

## References

- [Transactional batch operations](https://learn.microsoft.com/azure/cosmos-db/nosql/transactional-batch)
- [Transactions and optimistic concurrency](https://learn.microsoft.com/azure/cosmos-db/database-transactions-optimistic-concurrency)
- [Service quotas and limits](https://learn.microsoft.com/azure/cosmos-db/concepts-limits)
- [Hierarchical partition keys](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys)
- Implementation: `Internal/CosmosDbTransaction.cs`
