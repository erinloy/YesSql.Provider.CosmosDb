# Conformance and tests

The provider is checked against YesSql's own test suite (`CoreTests`, v5.4.7), the suite the SQL Server, PostgreSQL, MySQL and SQLite providers run. It also has its own tests for behavior that is specific to Cosmos.

## Results

All 249 tests in `CoreTests` pass on both partition strategies against the Cosmos DB emulator, and against a real serverless account (West US, gateway mode). The YesSql 6.0.0 suite passes on the emulator on both strategies.

The real-account run found a difference between the emulator and the service: Cosmos DB compares `DateTimeToTimestamp(c.x)` wrongly in a `WHERE` clause when the stored text has a UTC offset east of +01:00. YesSql's `AllDataTypesShouldBeQueryable` tests store a `+01:02` offset and failed on the real account until the provider began storing moments in time as UTC. The emulator never showed it, so CI would not catch a similar difference.

The suite covers document CRUD, map and reduce indexes and their update and delete lifecycle, queries over one or several indexes (including map plus reduce), the raw `INNER`, `LEFT` and `RIGHT JOIN` count API, comparison and `IN` predicates, date and decimal functions, ordering, paging, counts, `byte[]` index columns, `RenameColumn`, optimistic concurrency and rollback.

The two strategies differ on rollback. With `PerStore` the undo log is applied as transactional batches, which are atomic one batch at a time. With `PerTable` it is applied item by item. The rollback tests in `CoreTests` pass on both, but they use small units of work and do not exercise the limits described in [PARTITIONING.md](PARTITIONING.md).

## Running the conformance suite

The project in `test/Conformance` source-links YesSql's test sources and compiles them against the same `YesSql 5.4.7` package that the provider references, so there is a single YesSql assembly.

`CoreTests` uses YesSql internals (`Session._commands`, `NullableThumbprintFactory`). YesSql exposes those to an assembly named `YesSql.Tests` signed with its own key, so the conformance assembly takes that name and is signed with `YesSqlKey.snk` from the YesSql sources.

1. Start the emulator. See the [README](../README.md#running-against-the-emulator).
2. Fetch the YesSql sources into `external/yessql`:

   ```bash
   pwsh scripts/clone-yessql.ps1
   ```

3. Run the suite. It takes a few minutes.

   ```bash
   dotnet test test/Conformance/YesSql.Provider.CosmosDb.Conformance.csproj

   # PerStore run
   COSMOS_PARTITION=PerStore dotnet test test/Conformance/YesSql.Provider.CosmosDb.Conformance.csproj
   ```

To run the suite against a real account, set `COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY`. Without them the suite uses the emulator and its published key. The key is never sent to a named endpoint unless you set it, certificate validation is skipped only for a loopback endpoint, and the run creates one database named `yessql_conf_*` that it does not delete. A full run costs a fraction of a dollar on a serverless account and takes about 18 minutes per strategy from West US. The provider tests in `test/YesSql.Provider.CosmosDb.Tests` read the same two variables.

To use an existing YesSql checkout, pass `-p:YesSqlTestsDir=<path to test/YesSql.Tests>`. The signing key is read from `src/YesSqlKey.snk` two directories above that path.

`CosmosTests` (in `test/Conformance/CosmosTests.cs`) derives from `CoreTests`. It points the configuration at the emulator with one database per run, and its cleanup hooks delete the container's items instead of running `DELETE FROM <table>`. The first run waits until the emulator answers a real request, because the emulator can report its gateway as up before its query engine is ready.

## YesSql 6

The package is built against YesSql 5.4.7. `test/Conformance.YesSql6` runs YesSql 6.0.0's `CoreTests` against that same build, with YesSql 6 supplied by the test project, which is what an application that uses YesSql 6 gets. It uses the same `CosmosTests` class as the YesSql 5 project.

YesSql 6's tests use xunit v3, so the project is an executable:

```bash
pwsh scripts/clone-yessql.ps1 -Tag v6.0.0 -Directory yessql6
dotnet build test/Conformance.YesSql6/YesSql.Provider.CosmosDb.Conformance.YesSql6.csproj -c Release
dotnet ./test/Conformance.YesSql6/bin/Release/net10.0/YesSql.Tests.dll

# PerStore run
COSMOS_PARTITION=PerStore dotnet ./test/Conformance.YesSql6/bin/Release/net10.0/YesSql.Tests.dll
```

The YesSql 6 run also includes YesSql's filter and utility tests, so its total is larger than the 249 `CoreTests` that YesSql 5.4.7 has. The classes that need a database server, such as the SQL Server and PostgreSQL fixtures, are excluded.

## Provider tests

`test/YesSql.Provider.CosmosDb.Tests` covers Cosmos-specific behavior directly:

| Tests | Covers |
| --- | --- |
| `CrudTests`, `CosmosRoundTripTests` | Document save, load, update and delete |
| `IndexQueryTests`, `QueryFeaturesTests` | Index queries, ordering, paging |
| `RollbackTests` | Rollback on both strategies |
| `ReplaceUpdateTests` | `UPDATE ... SET col = REPLACE(...)` statements |
| `IndexingTaskRoundTripTests` | Identity-table inserts and queries of the kind Orchard Core uses for content indexing |

`OrchardCosmosVerify` is a skipped diagnostic. It lists what the Orchard Core sample wrote to the emulator.

```bash
dotnet test test/YesSql.Provider.CosmosDb.Tests
```

## Scope

Passing `CoreTests` means the provider handles every operation that suite exercises. It does not cover SQL that YesSql users issue by hand through the session's connection, workloads at production scale, or a live Cosmos account. See the limitations in the [README](../README.md#limitations).
