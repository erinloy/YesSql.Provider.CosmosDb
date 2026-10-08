NOTICE: AI GENERATED SLOP. KNOWN TO WORK, BUT BARELY REVIEWED. TAKE APPROPRIATE PERCAUTIONS IN YOUR DOWNSTREAM WORKS.

# YesSql.Provider.CosmosDb

[![CI](https://github.com/erinloy/YesSql.Provider.CosmosDb/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/erinloy/YesSql.Provider.CosmosDb/actions/workflows/ci.yml)
[![Release](https://github.com/erinloy/YesSql.Provider.CosmosDb/actions/workflows/release.yml/badge.svg)](https://github.com/erinloy/YesSql.Provider.CosmosDb/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/YesSql.Provider.CosmosDb.svg)](https://www.nuget.org/packages/YesSql.Provider.CosmosDb)
[![NuGet downloads](https://img.shields.io/nuget/dt/YesSql.Provider.CosmosDb.svg)](https://www.nuget.org/packages/YesSql.Provider.CosmosDb)
[![License: MIT](https://img.shields.io/github/license/erinloy/YesSql.Provider.CosmosDb.svg)](https://github.com/erinloy/YesSql.Provider.CosmosDb/blob/master/LICENSE)
![.NET 8 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)
![YesSql 5.4.7 | 6.0.0](https://img.shields.io/badge/YesSql-5.4.7%20%7C%206.0.0-blue)
![Orchard Core 3.0](https://img.shields.io/badge/Orchard%20Core-3.0.x-blue)

A [YesSql](https://github.com/sebastienros/yessql) storage provider for [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) (NoSQL API). It lets YesSql, and applications built on it such as [Orchard Core](https://orchardcore.net/), keep their documents and indexes in a Cosmos DB container.

YesSql ships providers for SQL Server, PostgreSQL, MySQL and SQLite. This package adds Cosmos DB without forking YesSql.

## How it works

YesSql has no storage layer below the session. It builds SQL text with an `ISqlDialect` and runs it through Dapper on an ADO.NET `DbConnection` that `IConfiguration.ConnectionFactory` supplies. This package implements the three extension points YesSql offers there (`ConnectionFactory`, `SqlDialect`, `CommandInterpreter`) and changes nothing in YesSql.

- **A Cosmos-backed ADO.NET implementation.** The connection, command, reader and transaction classes are internal. A command parses the SQL text it is given into a statement tree and runs it as Cosmos operations: point reads, creates, replaces, deletes and queries inside one partition.
- **It recognizes the SQL that YesSql and Orchard Core emit, not SQL in general.** A statement outside that grammar throws, and so does a condition the provider cannot apply. Nothing is skipped or dropped.
- **Rows are items.** Every table of YesSql lives in one container as items `{ id: "<table>:<Id>", pk, __table, <columns> }`. Index rows hold their `DocumentId`, and a reduce index's link table holds one item for each link.
- **Schema commands do nothing, on purpose.** A container has no schema: a table or column exists when an item that has it is written, and Cosmos indexes every property. The one exception is `RenameColumn`, which rewrites the field in every item of the table. Dropping a column or table leaves its data in place. See [Schema commands](docs/ARCHITECTURE.md#schema-commands).
- **Transactions are an undo log, not isolation.** Writes are applied as they are issued, so YesSql's autoflush sees them, and each records its inverse. Rolling back replays the inverses. With `PerStore` the replay is transactional batches; with `PerTable` it is item by item. See [Partition strategies](#partition-strategies) and [docs/PARTITIONING.md](docs/PARTITIONING.md).
- **Ids.** Document ids come from YesSql's id generator, which works unchanged (`UseDefaultIdGenerator` and `UseBlockIdGenerator`). Index row ids come from a counter item per table that processes lease in blocks of 32.

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) has the details, and [docs/YESSQL-COUPLING.md](docs/YESSQL-COUPLING.md) lists what the provider assumes about the SQL YesSql generates, so that a change in YesSql which breaks an assumption fails a test here.

## Status

Preview (0.1.x). Interfaces and behavior may still change.

- YesSql's own test suite (`CoreTests`) passes in full against the Cosmos DB emulator on both partition strategies: 249 of 249 tests for YesSql 5.4.7, and the 6.0.0 suite (the tests that do not need a database server). The 5.4.7 suite also passes 249 of 249 against a real serverless account on both strategies. See [docs/CONFORMANCE.md](docs/CONFORMANCE.md).
- A minimal Orchard Core 3.0.1 site runs its setup recipe, serves requests and passes content checks (create, publish, draft, unpublish, remove, ordered and paged queries, concurrent creates, cancelled and failed requests) with this provider as its only data store. See [docs/ORCHARD-INTEGRATION.md](docs/ORCHARD-INTEGRATION.md).
- Read [Limitations](#limitations) before using this with real data.

## Installation

```bash
dotnet add package YesSql.Provider.CosmosDb
```

Targets `net8.0` and `net10.0`.

### Compatibility

| Orchard Core | YesSql | Sample status |
| --- | --- | --- |
| 3.0.x (stable) | 5.4.7 | Sets up and serves from Cosmos. Run in CI. |
| 4.0.0 preview (`4.0.0-preview-19175`) | 6.0.0 | Sets up and serves from Cosmos. Run in CI as a non-gating check, because previews change. |

The package is built against YesSql 5.4.7 and works with YesSql 6.0.0: YesSql 6.0.0's own tests pass against it on both partition strategies. No stable Orchard Core release uses YesSql 6 yet.

Orchard Core and YesSql versions have to match. Orchard Core 3.0.x was built against YesSql 5.4.7 and calls overloads that YesSql 6.0.0 removed. Adding YesSql 6.0.0 to a project that uses Orchard Core 3.0.x fails at startup with a `MissingMethodException` (for example on `IStore.InitializeCollectionAsync`), thrown from Orchard Core's own data access setup. Without an explicit YesSql reference, Orchard Core 3.0.x and this package both resolve YesSql 5.4.7. If you see this exception, check which YesSql version your application actually restores (`dotnet list package --include-transitive`), and use 5.4.7 with Orchard Core 3.0.x.

[`samples/OrchardSmokeTest`](samples/OrchardSmokeTest) builds against either line. Select one with `-p:OrchardCoreVersion=<version>`; the default is 3.0.1. See [Orchard Core integration](docs/ORCHARD-INTEGRATION.md).

## Usage

```csharp
using Microsoft.Azure.Cosmos;
using YesSql;
using YesSql.Provider.CosmosDb;

var configuration = new Configuration()
    .UseCosmosDb(new CosmosDbOptions
    {
        AccountEndpoint = "https://my-account.documents.azure.com:443/",
        AccountKey = "<key>",
        DatabaseId = "myapp",
    })
    .UseDefaultIdGenerator();

var store = await StoreFactory.CreateAndInitializeAsync(configuration);

await using var session = store.CreateSession();
await session.SaveAsync(new Person { Name = "Alice" });
await session.SaveChangesAsync();
```

The database and container are created on first use unless `CreateIfNotExists` is `false`.

### Options

| Option | Default | Description |
| --- | --- | --- |
| `AccountEndpoint`, `AccountKey` | required | Cosmos account endpoint and key. |
| `DatabaseId` | required | Database that holds the store. |
| `ContainerId` | `yessql` | Container for all documents and index rows. |
| `PartitionKeyPath` | `/pk` | Single-level path such as `/pk` or `/tenantId`. Items store their partition key in that property. An existing container must use the same path. |
| `PartitionStrategy` | `PerTable` | How items map to logical partitions. See below. |
| `PartitionScope` | `store` | Partition key value used by `PerStore`, for example a tenant name. |
| `CreateIfNotExists` | `true` | Create the database and container if they do not exist. |
| `ClientOptions` | `null` | `CosmosClientOptions` passed to the SDK client. Needed for the emulator. One SDK client is shared for each account endpoint and key for the life of the process, and the options of the first store that opens it apply. |

### Partition strategies

Cosmos DB can only commit atomically within a single logical partition. A YesSql unit of work writes several items (the document, its index rows and any bridge rows), so the partitioning choice decides what rollback can guarantee.

| Strategy | Partition key | Rollback of a unit of work | Scale limit |
| --- | --- | --- | --- |
| `PerTable` (default) | YesSql table name | Best effort, item by item | 20 GB and 10,000 RU/s for each YesSql table, so for all documents of a collection |
| `PerStore` | `PartitionScope` | One transactional batch per 100 items changed | 20 GB and 10,000 RU/s per store |

`PerStore` fits workloads with bounded data per store, such as one Orchard Core tenant per `PartitionScope`. A rollback is atomic only while it fits in one batch and the batch is accepted; the exact guarantee and its limits are in [docs/PARTITIONING.md](docs/PARTITIONING.md).

## Limitations

### SQL

- The provider is not a SQL engine. YesSql talks to it through ADO.NET and SQL text, and it recognizes the statement shapes that YesSql and Orchard Core generate. A statement outside the SQL it understands is rejected with a `DbException` that names the position of the problem, and one it parses but cannot run throws `NotSupportedException`. A few misuse errors (a missing parameter, a partition key path that does not match the container) throw `InvalidOperationException`. The statement shapes are listed in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#statement-handling).
- Nothing in a statement is skipped. A condition the provider cannot apply, or a join it cannot answer, throws.
- `INSERT` creates an item and fails with a `CosmosDbException` (status 409) if the id exists, as a duplicate key does in a relational database. `UPDATE` changes one row selected by `@Id` and affects 0 rows when it is not there.
- Comparisons in a `WHERE` clause (`=`, `LIKE`, `IN`) follow Cosmos DB, which compares strings case-sensitively. Ordering by an index column is case-insensitive, like the relational providers.
- Cosmos DB numbers are 64-bit floating point. Ids and counts are far below the point where that matters (2^53), but a `long` or `decimal` index column outside it has not been tested and may lose precision.

### Schema and ids

- Schema commands do nothing, except `RenameColumn`, which rewrites the field in every item and is not undone by a rollback. Raw SQL that a migration sends with `SchemaBuilder.ExecuteSql` is ignored, because there is nothing to run it against. Dropping a table or a column does not delete data, and nothing enforces primary keys, unique constraints or foreign keys.
- `UseDefaultIdGenerator` keeps its counter in memory, seeded from `MAX(Id)` when the store starts, as it does with every provider, so two processes that write to one store at the same time need `UseBlockIdGenerator`, which leases ids from the store with a conditional write and is safe across processes.

### Transactions

- There is no isolation between sessions. Writes are applied as they happen, so another session can read changes from a unit of work that has not committed. On rollback the provider restores the previous version of each item, which can overwrite a concurrent writer's changes to the same item.
- A crash before commit leaves the writes in place, because the undo log is in memory.
- With `PerTable`, a failed unit of work is rolled back item by item, and a crash during rollback can leave partial writes. With `PerStore` a rollback is atomic for each batch of up to 100 items.
- A write that Cosmos rejects is reported by a later statement or by the commit, not by the statement that issued it, because writes inside a unit of work are started without waiting for the response.

### Cost and speed

- A count, a first match, or a page ordered by document id (the order YesSql gives a paged query that has no order of its own) over one map index is answered by Cosmos from the distinct document ids. Orders on an index column, queries through a reduce index or several indexes, and queries that filter on the document type read every matching index row to the client, then order and page there, so their request unit cost grows with the number of matching index rows. At 500 rows per key on a real account, a count cost 7 RU and an ordered page of 20 documents cost 24 RU.
- An order on an index column cannot be left to Cosmos: it sorts text case-sensitively, where YesSql's other providers do not, and it sorts the stored date text, which is wrong for times with fractions of a second.
- A save makes about 6 requests for a document with a map and a reduce index (50 RU before ids were reserved in blocks, 38 RU now). Requests that do not depend on each other overlap, so a save waits for about 3 round trips in a row. An update or delete first reads the index rows it changes, and those reads cannot overlap, so it takes more.

### Scale

- `PerStore` limits the whole store to 20 GB of data and 10,000 RU/s. `PerTable` limits each YesSql table to the same, which means all the documents of a collection.

### Accounts and clients

- Authentication is the account key (`CosmosDbOptions.AccountKey`). There is no support for Microsoft Entra ID or managed identity.
- The provider sets no consistency level, so the account's default applies, and it leaves throttling (429) to the Cosmos SDK's default retry policy. Both can be changed through `CosmosDbOptions.ClientOptions`, and the first `ClientOptions` used for an account apply to every store that uses it in the process.

### Evidence

- The automated tests in CI run against the emulator. The 5.4.7 conformance suite has also been run against one real serverless account (West US) with the code of version 0.1.5; that run found a date comparison the emulator does not reproduce (see the changelog). Behavior and request unit cost on a live account at scale have not been measured, and the later changes (writes started without waiting, stricter statements) have not been run against one.

## Running against the emulator

Development uses the Linux emulator image (`vnext-preview`). It serves plain HTTP on port 8081, so use `http://localhost:8081/`. Use gateway mode. Emulators that serve HTTPS present a self-signed certificate, which has to be accepted explicitly:

```bash
docker run -d --name cosmos-emu -p 8081:8081 -p 10250-10255:10250-10255 \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

```csharp
ClientOptions = new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
    LimitToEndpoint = true,
    // Emulator only. Skip certificate validation for a loopback endpoint and never for a real account.
    HttpClientFactory = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback
        ? () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        : null,
}
```

Do not use `DangerousAcceptAnyServerCertificateValidator` with a real Cosmos DB account. It turns off certificate validation for the connection that carries the account key.

The account key in this repository's tests and sample is the emulator's published default key. It is not a secret.

## Building and testing

```bash
dotnet build YesSql.Provider.CosmosDb.slnx

# Provider tests (emulator must be running)
dotnet test test/YesSql.Provider.CosmosDb.Tests

# YesSql's CoreTests against Cosmos. Fetch the YesSql sources first.
pwsh scripts/clone-yessql.ps1
dotnet test test/Conformance/YesSql.Provider.CosmosDb.Conformance.csproj
```

See [docs/CONFORMANCE.md](docs/CONFORMANCE.md) for how the conformance project works, including the `PerStore` run.

## Documentation

- [Architecture](docs/ARCHITECTURE.md): storage model and how SQL is translated to Cosmos operations
- [What the provider assumes about YesSql](docs/YESSQL-COUPLING.md)
- [Partitioning and transactions](docs/PARTITIONING.md)
- [Conformance and tests](docs/CONFORMANCE.md)
- [Orchard Core integration](docs/ORCHARD-INTEGRATION.md)
- [Changelog](CHANGELOG.md)

## Contributing

Issues and pull requests are welcome. Before submitting a change that touches `CosmosDbCommand` or `CosmosDbTransaction`, where SQL is translated and writes are run, run the provider tests and both conformance projects (YesSql 5.4.7 and 6.0.0) against the emulator, on both partition strategies. CI runs them on every pull request, together with the Orchard Core sample.

## License

MIT. See [LICENSE](LICENSE).

This is an independent project and is not affiliated with Microsoft, the YesSql project or the Orchard Core project.
