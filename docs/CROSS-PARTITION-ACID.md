# Cross-partition ACID on Cosmos — deep investigation

## The constraint (verified against Microsoft docs)

Azure Cosmos DB has **no cross-partition transactions, by design** — atomically updating two partitions
would require a cross-server two-phase commit, which would break Cosmos's latency SLA. Atomicity exists
**only within a single logical partition**, via two mechanisms:

| Mechanism | Scope | Limits | Rollback |
|---|---|---|---|
| **Transactional batch** | one logical partition, one container | ≤ 100 operations, ≤ 2 MB, ≤ 5 s | all-or-nothing |
| **Stored procedure** (JS) | one logical partition | no op-count cap; ≤ 5 s + RU budget | aborts & rolls back on `throw` |

A logical partition itself is capped at **20 GB** and **10,000 RU/s**.

## Why this provider hits it

A YesSql unit of work (one `ISession` flush) writes a **set** of items: the `Document`(s), each map-index
row, reduce-index rows, and bridge rows. This provider currently sets `pk = <table name>` (Document,
UserIndex, UserByRoleNameIndex_Document, …). So a single unit of work spans **many logical partitions**
→ no atomic boundary → Orchard's `IDocumentStore.CancelAsync()` (rollback on request exception) cannot
undo partial writes. That is the entire "no rollback" limitation.

## Options

### A. One logical partition per tenant → TRUE ACID (recommended for Orchard)
Set `pk` to a **per-store/tenant constant** (e.g. the shell name) instead of the table name. Every item a
unit of work touches then shares one logical partition, so the flush can commit as **one stored
procedure** (preferred over a transactional batch — no 100-op cap) → genuine ACID with `throw`-rollback.

- **Caps become per-tenant:** 20 GB and 10,000 RU/s. For Orchard this is the **metadata only** — images
  and files live in Blob/media, not the DB. 20 GB of YesSql documents is on the order of *millions* of
  content items; 10k RU/s is substantial. **For the overwhelming majority of Orchard sites these caps
  never bind.**
- **Per-flush size:** a stored proc sidesteps the 100-op batch cap (bounded only by 5 s / RU); a recipe
  import flushing thousands of ops may still need chunking (losing strict atomicity for that one bulk
  import — acceptable, and not a request-rollback path).
- **Cross-doc queries still work:** queries already run per-partition; with one partition they're simply
  in-partition (cheaper). Index "tables" become a `Type`/discriminator within the single partition.

### B. Hierarchical partition keys `[tenant, …]`
Lets the first-level (tenant) key exceed 20 GB. **But** transactional/stored-proc atomicity is scoped to
the *full* hierarchical path, so to keep a unit of work atomic the table/doc must NOT be a lower level —
which contradicts using the lower level for scale. HPK solves *scale past 20 GB*, not *cross-table ACID*.
Useful only if a single tenant genuinely exceeds 20 GB.

### C. Compensation / saga (write-ahead intent log)
Keep the scalable `pk = table` model; make rollback **best-effort**: before writing, append an intent/undo
record; on failure, run compensations (delete inserts, restore prior versions from the undo record).
Eventual, complex, and *not* true ACID (a crash between write and compensation leaves partial state). It
approximates `CancelAsync` but adds RU + latency to every write. Not recommended unless scale forces `pk=table`.

### D. Accept it (current behavior)
Eager writes, no rollback, documented. In practice most Orchard requests commit successfully; the gap is
only the error path. This is the honest status quo.

## The fundamental trade-off

Cosmos forces a choice between **ACID rollback** and **unbounded horizontal scale** for a multi-item unit
of work — you cannot have both. The good news for *Orchard specifically*: its per-tenant data is bounded
metadata, so **Option A buys true ACID at a 20 GB / 10k RU/s ceiling that typical Orchard sites never
reach** — the limitation is solvable for the real workload, not just theoretically.

## Recommendation

Offer a **partitioning mode** on `CosmosDbOptions`:
- `PartitionStrategy.PerStore` (default for Orchard) — `pk = store/tenant`, unit-of-work committed via a
  partition-scoped **stored procedure** → full ACID + `CancelAsync` rollback. Document the 20 GB / 10k RU/s
  per-tenant ceiling.
- `PartitionStrategy.PerTable` (current) — `pk = table`, horizontally scalable, **no cross-table rollback**
  (best-effort only). For very large single tenants that trade rollback for scale.

### Implementation sketch (PerStore + ACID)
1. `pk = options.PartitionScope` (constant) for all items; keep the table name as a `Type`/`__table`
   discriminator field for query routing.
2. Buffer the session's `IIndexCommand`s during the flush (the ADO.NET shim's `DbTransaction` already
   models the unit of work) instead of writing eagerly.
3. On `Commit`, translate the buffered commands into a single **stored procedure** call (upserts/deletes)
   scoped to the partition — atomic, rolls back on error. (The `imranmomin/Hangfire.AzureCosmosDb`
   provider already demonstrates this exact pattern.)
4. On `Rollback` (`CancelAsync`), simply discard the buffer — nothing was written.
5. Chunk bulk flushes that exceed proc limits; log when strict atomicity is dropped for a chunked import.

**Effort:** medium — a new partition-scope option, a buffering write path in the shim, one stored proc,
and the read path keyed to the single partition. It converts the documented limitation into full ACID for
the common Orchard case.

## Sources
- [Cosmos transactional batch](https://learn.microsoft.com/azure/cosmos-db/nosql/transactional-batch)
- [Stored procedures / ACID scope](https://learn.microsoft.com/azure/cosmos-db/database-transactions-optimistic-concurrency)
- [Service quotas & limits](https://learn.microsoft.com/azure/cosmos-db/concepts-limits)
- [Hierarchical partition keys](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys)
