# Conformance status

The provider is validated against **YesSql's own test suite** (`CoreTests`, v5.4.7), the same suite
the first-party SQL Server / PostgreSQL / MySQL / SQLite providers pass.

**Current: 202 / 249 passing (81%).** Plus 8 hand-written provider tests, all green, 0 build warnings.

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

## What's left (47 failures) — by category and feasibility

| Bucket | ~Count | Feasibility |
| --- | --- | --- |
| **Reduce indexes** | ~10 | Tractable but **not yet working** — see below. Biggest bucket. |
| **Transactions / autoflush / rollback** | ~7 | **Cosmos-bounded** — needs command buffering + per-partition transactional batch + an undo-log for rollback. Cosmos has no cross-partition ACID; partial at best. |
| **Multi-index joins** (`CanRunInner/Left/RightJoin`, `ShouldJoinMapIndexes`) | ~6 | **Cosmos-bounded** — no cross-item/-partition JOIN; multi-step emulation only. |
| **Ordering edge cases** (case-insensitive, value-type, dedup) | ~4 | Mixed; case-insensitive fights Cosmos's case-sensitive `ORDER BY`. |
| **SQL functions** (`year()`/`month()`/decimal/`now()`) | ~3 | Largely **N/A** to a NoSQL store. |
| **Misc** (binary-in-index, DateTimeOffset compare, rename-column DDL, subclasses) | ~7 | Varied / niche. |

### Reduce indexes — the next target (needs trace-driven debugging)

A reduce index (e.g. `ArticlesByDay { DayOfYear, Count }`) aggregates many documents into one index
row per group key, with a **bridge table** linking that row to its contributing documents.

**Observed failure:** in `ShouldReduce`, `QueryIndex<ArticlesByDay>().CountAsync()` returns **0**
(expected 4) — the reduce index rows are **not persisted at all**. `SaveChangesAsync` succeeds but no
effective index `INSERT` lands. This is a *silent* failure (no exception), so it can't be diagnosed by
the translate-and-rerun loop used for the other buckets.

**Plan:**
1. Enable YesSql trace logging (`configuration.UseLogger(...)` / `EnableLogging`) during a single
   reduce save and capture the exact command sequence + SQL YesSql emits for `ReduceIndex`.
2. Implement the reduce **write** path: group-key aggregation (one index row per key, `Count` merged
   on update) + **composite-key bridge rows** (`<bridge>:<indexId>:<docId>` — a non-composite key
   collapses multiple docs into one bridge row; this was attempted and is necessary but insufficient
   alone, so it was reverted to keep the suite at a clean 202).
3. Implement the reduce **read** path: the doc↔bridge↔index three-way join becomes a multi-step
   Cosmos lookup (index by group key → bridge by `<IndexName>Id` → point-read documents).

### Transactions / rollback — Cosmos limitation

YesSql expects writes within a `Session` to be undone if `SaveChangesAsync` is never called
(`NoSavingChangesShouldRollbackAutoFlush`). The provider currently writes eagerly and its
`DbTransaction.Commit/Rollback` are no-ops, so uncommitted changes persist. Faithful rollback needs
either command buffering with read-your-writes, or an undo-log — and Cosmos only offers atomicity
**within a single logical partition** (transactional batch / stored procedure). True cross-partition
rollback is not achievable; this bucket will remain partial.

## Verdict

81% is a strong, shippable core for document + map-index + common-query workloads. The remaining 19%
is structural, and a meaningful share (transactions, cross-partition joins) is **bounded by Cosmos
itself** — "all green" is not reachable without semantic compromises. The highest-value next work is
reduce indexes (trace-driven), followed by CI-with-emulator and an Orchard Core smoke test.
