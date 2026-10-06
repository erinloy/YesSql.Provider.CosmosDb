NOTICE: AI GENERATED SLOP. KNOWN TO WORK, BUT BARELY REVIEWED. TAKE APPROPRIATE PERCAUTIONS IN YOUR DOWNSTREAM WORKS.

# YesSql.Provider.CosmosDb

A [YesSql](https://github.com/sebastienros/yessql) storage provider for [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) (NoSQL API). It lets YesSql, and applications built on it such as [Orchard Core](https://orchardcore.net/), keep their documents and indexes in a Cosmos DB container.

YesSql ships providers for SQL Server, PostgreSQL, MySQL and SQLite. This package adds Cosmos DB without forking YesSql.

## Status

Preview (0.1.x). Interfaces and behavior may still change.

- YesSql's own test suite (`CoreTests`) passes in full against the Cosmos DB emulator on both partition strategies: 249 of 249 tests for YesSql 5.4.7, and the 6.0.0 suite (the tests that do not need a database server). See [docs/CONFORMANCE.md](docs/CONFORMANCE.md).
- A minimal Orchard Core 3.0.1 site runs its setup recipe and serves requests with this provider as its only data store. See [docs/ORCHARD-INTEGRATION.md](docs/ORCHARD-INTEGRATION.md).
- Read [Limitations](#limitations) before using this with real data.

## Installation

```bash
dotnet add package YesSql.Provider.CosmosDb
```

Targets `net8.0` and `net10.0`.

### Compatibility

| | Supported |
| --- | --- |
| YesSql | 5.4.7 and 6.0.0. The package is built against 5.4.7, and YesSql 6.0.0's own tests also pass against it. |
| Orchard Core | 3.0.x, which uses YesSql 5.4.7. |

Orchard Core 3.0.x was built against YesSql 5.4.7. If an application references YesSql 6.0.0 next to Orchard Core 3.0.x, Orchard fails at startup with a `MissingMethodException` (for example on `IStore.InitializeCollectionAsync`). The exception is thrown by Orchard Core's own data access setup, not by the database provider. Keep YesSql at 5.4.7 with Orchard Core 3.0.x. Versions of Orchard Core that use YesSql 6 have not been tested with this provider.

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
| `ClientOptions` | `null` | `CosmosClientOptions` passed to the SDK client. Needed for the emulator. |

### Partition strategies

Cosmos DB can only commit atomically within a single logical partition. A YesSql unit of work writes several items (the document, its index rows and any bridge rows), so the partitioning choice decides what rollback can guarantee.

| Strategy | Partition key | Rollback of a unit of work | Scale limit |
| --- | --- | --- | --- |
| `PerTable` (default) | YesSql table name | Best effort, item by item | None beyond Cosmos itself |
| `PerStore` | `PartitionScope` | One transactional batch per 100 items changed | 20 GB and 10,000 RU/s per store |

`PerStore` fits workloads with bounded data per store, such as one Orchard Core tenant per `PartitionScope`. A rollback is atomic only while it fits in one batch and the batch is accepted; the exact guarantee and its limits are in [docs/PARTITIONING.md](docs/PARTITIONING.md).

## Limitations

- The provider is not a SQL engine. YesSql talks to it through ADO.NET and SQL text, and it recognizes the statement shapes that YesSql and Orchard Core generate. Other statements generally fail with `NotSupportedException`.
- There is no isolation between sessions. Writes are applied as they happen, so another session can read changes from a unit of work that has not committed. On rollback the provider restores the previous version of each item, which can overwrite a concurrent writer's changes to the same item.
- With `PerTable`, a failed unit of work is rolled back item by item, and a crash during rollback can leave partial writes.
- Queries that join an index to its documents first collect the matching document ids from the index partition, apply any ordering and paging in the client, and then read the page's documents. Request unit cost grows with the number of matching index rows.
- `PerStore` limits the whole store to 20 GB of data and 10,000 RU/s.
- The automated tests run against the emulator. Behavior and request unit cost on a live account at scale have not been measured.

## Running against the emulator

Development uses the Linux emulator image (`vnext-preview`). It serves plain HTTP on port 8081, so use `http://localhost:8081/`. Use gateway mode and accept its self-signed certificate:

```bash
docker run -d --name cosmos-emu -p 8081:8081 -p 10250-10255:10250-10255 \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

```csharp
ClientOptions = new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
    LimitToEndpoint = true,
    HttpClientFactory = () => new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }),
}
```

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
- [Partitioning and transactions](docs/PARTITIONING.md)
- [Conformance and tests](docs/CONFORMANCE.md)
- [Orchard Core integration](docs/ORCHARD-INTEGRATION.md)
- [Changelog](CHANGELOG.md)

## Contributing

Issues and pull requests are welcome. Run both test projects against the emulator before submitting a change that touches `CosmosDbCommand`, since that is where SQL is translated.

## License

MIT. See [LICENSE](LICENSE).

This is an independent project and is not affiliated with Microsoft, the YesSql project or the Orchard Core project.
