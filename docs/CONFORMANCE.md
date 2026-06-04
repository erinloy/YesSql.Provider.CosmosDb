# Conformance status

The provider is validated against **YesSql's own test suite** (`CoreTests`, v5.4.7), the same suite
the first-party SQL Server / PostgreSQL / MySQL / SQLite providers pass.

**Current: 249 / 249 passing (100%)** — verified on **both** the `PerTable` and `PerStore` partition
strategies (the suite self-warms the emulator before measuring; see "Running the conformance suite").
Plus the hand-written provider tests (incl. rollback), all green.
**Validated end-to-end: Orchard Core 2.2.1 boots and runs on this provider** (see ORCHARD-INTEGRATION.md).

## Coverage

**Every operation YesSql (and therefore Orchard) exercises is supported:** document CRUD; map indexes and
their update/delete lifecycle; reduce indexes (aggregate, merge, query); single- and multi-index
(`.With<I1>().With<I2>()`) queries, **including map+reduce intersection** (`.With<Map>().With<Reduce>()`);
the **raw `INNER`/`LEFT`/`RIGHT JOIN` count API** (cross-document joins emulated by gather-then-point-read);
`Where`/range/boolean/`IS NULL`/`IN`-subquery predicates; **`DateTime`/`DateTimeOffset` comparison by
instant** (cross-type, via `DateTimeToTimestamp`); `OrderBy` (case-insensitive, matching the reference
dialects); paging; `CountAsync` (scalar and via the reader path); SQL date-part / `now()` / decimal type
functions; **`filterType` CLR-type polymorphism** (`Query<SubClass>(filterType: true)`); **`byte[]` index
columns** (self-describing base64 round-trip); **`RenameColumn` DDL** (data rewrite) and literal-value
`INSERT`; monotonic (append-only) index ids; **optimistic concurrency** (version check + ETag, so
`ConcurrencyException` is raised on stale/concurrent writes); and **unit-of-work rollback** (undo log —
atomic in `PerStore`, best-effort in `PerTable`).

The one previously Orchard-relevant gap — request rollback / concurrency — is now **closed** (rollback
via the undo log, concurrency via version+ETag). True cross-partition ACID remains impossible on Cosmos;
`PerStore` makes a unit of work single-partition so its rollback is atomic (see CROSS-PARTITION-ACID.md).

## Running the conformance suite

The harness lives in `test/Conformance`. It **source-links** YesSql's v5.4.7 `CoreTests` (and its
models/indexes) and compiles them against the same NuGet `YesSql 5.4.7` the provider references — one
assembly, no version conflict. To reach YesSql internals that `CoreTests` uses (`Session._commands`,
`NullableThumbprintFactory`), the conformance assembly is named `YesSql.Tests` and signed with
`YesSqlKey.snk` to satisfy YesSql's `[InternalsVisibleTo("YesSql.Tests", PublicKey=…)]`.

```bash
# 1. start the emulator (HTTP on :8081 — see README)
docker run -d --name cosmos-emu -p 8081:8081 -p 10250-10255:10250-10255 \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview

# 2. run the suite (~4 minutes)
dotnet test test/Conformance/YesSql.Provider.CosmosDb.Conformance.csproj -c Debug
```

`CosmosTests : CoreTests` (in `test/Conformance/CosmosTests.cs`) overrides `CreateConfiguration`
(points at the emulator, one database per run) and the clean/clear hooks (each test wipes the
container instead of the raw `DELETE FROM <table>` `CoreTests` uses).

> The conformance project's source-link path (`YesSqlTestsDir` in the `.csproj`) currently points at a
> local YesSql v5.4.7 checkout. Adjust it if your checkout lives elsewhere.

## What works

- Document CRUD: save, load-by-id, update (read-and-patch), delete.
- Map indexes: write, update, delete-by-`DocumentId`.
- Queries: `FirstOrDefaultAsync`, `ListAsync`, `CountAsync`; equality, range/comparison, boolean
  `AND`/`OR`, `IS [NOT] NULL`; `OrderBy` (asc/desc); `OFFSET`/`LIMIT` paging.
- `IN`/`NOT IN` subqueries (resolved by pre-executing the inner query).
- Document-by-`Type` queries (`Query<T>()`), index-row queries (`Query<TIndex>()`).
- **Reduce indexes — full lifecycle** (`ShouldReduce`, `ShouldQueryByReducedIndex`,
  `UpdatingDocumentShouldUpdateReducedIndex`, `ShouldReduceAndMergeWithDatabase`, `ShouldAddGroupKey`,
  `ShouldRemoveGroupKey`, `ShouldJoinReduceIndex`, …): aggregated index rows are written, composite-key
  bridge rows link them to documents, the doc↔bridge↔index three-way query/count resolves, and
  merge/update/delete on subsequent saves keep the aggregate and bridge rows correct.

## What's left

Nothing within YesSql's `CoreTests` suite — all 249 pass on both partition strategies. The buckets that
were previously failing (reduce-index lifecycle, transactions/autoflush/rollback, multi-index and
LEFT/RIGHT joins, ordering edge cases, SQL date/decimal functions, binary-in-index, `DateTimeOffset`
compare, rename-column DDL, subclasses) are all now covered. See the "What works" matrix above and the
git history (207 → 224 → 226 → 229 → 242 → 249) for the progression.

### Structural limit — cross-partition ACID (not a test failure)

The one thing Cosmos genuinely cannot do is **atomic ACID across more than one logical partition**. A
YesSql unit of work writes a *set* of items (document + index rows + bridge rows); under `PerTable`
those span partitions, so rollback on error is **best-effort per item**. Under `PerStore` the whole
unit of work shares one logical partition, so its rollback is **atomic** (undo log applied via a Cosmos
transactional batch — see `Internal/CosmosDbTransaction.cs` and CROSS-PARTITION-ACID.md). That makes
`NoSavingChangesShouldRollbackAutoFlush` and the dedicated rollback tests pass on `PerStore`; the cost
is the per-logical-partition 20 GB / 10,000 RU/s ceiling, which typical Orchard tenants never reach.

## Verdict

100% of YesSql's own conformance suite passes on both partition strategies, plus the hand-written
provider tests and an end-to-end Orchard Core boot. The provider is a complete, shippable implementation
for document + map-index + reduce-index + query + rollback workloads. The only remaining trade-off is
architectural — true cross-partition ACID — and `PerStore` resolves it for the bounded-tenant case.
