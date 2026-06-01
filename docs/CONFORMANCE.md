# Conformance status

The provider is validated against **YesSql's own test suite** (`CoreTests`, v5.4.7), the same suite
the first-party SQL Server / PostgreSQL / MySQL / SQLite providers pass.

**Current: 229 / 249 passing (92%)** — PerTable and PerStore both. Plus 10 hand-written provider tests
(incl. rollback), all green, 0 build warnings. **Validated end-to-end: Orchard Core boots and runs on
this provider** (see ORCHARD-INTEGRATION.md).

## Orchard Core readiness

**Every operation Orchard exercises is supported:** document CRUD; map indexes and their update/delete
lifecycle; reduce indexes (aggregate, merge, query); single- and multi-index (`.With<I1>().With<I2>()`)
queries; `Where`/range/boolean/`IS NULL`/`IN`-subquery predicates; `OrderBy`; paging; `CountAsync`;
monotonic (append-only) index ids; **optimistic concurrency** (version check + ETag, so
`ConcurrencyException` is raised on stale/concurrent writes); and **unit-of-work rollback** (undo log —
atomic in `PerStore`, best-effort in `PerTable`). Orchard discriminates content types via a
`ContentType` *column* on its indexes (supported), not via YesSql's CLR `filterType` polymorphism.

The 20 remaining failures are **not Orchard operations** or are **bounded by Cosmos**:
- Raw `LEFT`/`RIGHT`/`INNER JOIN` count API (`CanRun*Join`, `ShouldJoinReduceIndex`,
  `ShouldOrderJoinedMapIndexes`) — Orchard uses `.With()`, not raw joins.
- `filterType` CLR-type polymorphism (`ShouldQuerySubClasses`) — Orchard uses the `ContentType` column.
- SQL `year()/month()/decimal/now()` functions, `RenameColumn` DDL — not used / N/A on a schemaless store.
- Case-insensitive `ORDER BY` (Cosmos ORDER BY is case-sensitive), binary-in-index, `DateTimeOffset` vs
  `DateTime` compare — niche / Cosmos-bounded.

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
- **Reduce indexes — initial save + query** (`ShouldReduce`, `ShouldQueryByReducedIndex`): aggregated
  index rows are written, composite-key bridge rows link them to documents, and the doc↔bridge↔index
  three-way query/count resolves correctly.

## What's left (42 failures) — by category and feasibility

| Bucket | ~Count | Feasibility |
| --- | --- | --- |
| **Reduce-index lifecycle** | ~7 | Partly done — see below. Merge/update/delete still broken. |
| **Transactions / autoflush / rollback** | ~7 | **Cosmos-bounded** — needs command buffering + per-partition transactional batch + an undo-log for rollback. Cosmos has no cross-partition ACID; partial at best. |
| **Multi-index joins** (`CanRunInner/Left/RightJoin`, `ShouldJoinMapIndexes`) | ~6 | **Cosmos-bounded** — no cross-item/-partition JOIN; multi-step emulation only. |
| **Ordering edge cases** (case-insensitive, value-type, dedup) | ~4 | Mixed; case-insensitive fights Cosmos's case-sensitive `ORDER BY`. |
| **SQL functions** (`year()`/`month()`/decimal/`now()`) | ~3 | Largely **N/A** to a NoSQL store. |
| **Misc** (binary-in-index, DateTimeOffset compare, rename-column DDL, subclasses) | ~7 | Varied / niche. |

### Reduce indexes — initial save + query DONE; lifecycle remains

A reduce index (e.g. `ArticlesByDay { DayOfYear, Count }`) aggregates many documents into one index
row per group key, with a **bridge table** linking that row to its contributing documents.

**Done** (trace-driven, see commits): the first save aggregates correctly (one index row per group),
composite-key bridge rows (`<bridge>:<indexFk>:<docId>`, with columns mapped from the INSERT's column
list since they differ from the param names — `[ArticlesByDayId]` ← `@Id`) link them to documents, and
the three-way doc↔bridge↔index query/count resolves (index by group key → bridge by `<IndexName>Id`
IN → point-read documents).

**Still broken — merge/update/delete lifecycle** (`UpdatingDocumentShouldUpdateReducedIndex`,
`ShouldReduceAndMergeWithDatabase`, `ShouldAddGroupKey`, `ShouldRemoveGroupKey`,
`Removing/AlteringDocumentShouldUpdateReducedIndex`, `ShouldJoinReduceIndex`).

**Observed:** save 2 docs (day1) → 1 index row, `Count=2` ✓. Then a *second* session saves 1 more
(day1): YesSql emits `update [ArticlesByDay] set [Count]=@Count, [DayOfYear]=@DayOfYear where [Id]=@Id`
+ a bridge insert — but the result is **2 index rows** (expected 1) with `Count=2` (expected 3). So the
merge `UPDATE` is landing on a *different* key than the existing row (likely creating a phantom row via
the update-path's read-or-create), and/or the merged `Count` is stale.

**Next:** add **param-value** tracing (the `ILogger` capture only sees SQL text, not `@Id`/`@Count`
values) — instrument `CosmosDbCommand` to log `CommandText` + parameter values for `tpArticlesByDay`
ops on the second save. Determine whether YesSql's merge `UPDATE` targets the existing `Id` (and my
update is creating a duplicate) or a fresh `Id` (and the load-by-group-key returned the wrong row).
Then fix the merge-update path and the reduce delete (`DeleteReduceIndexCommand` + bridge cleanup).

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
