# YesSql.Provider.CosmosDb

An [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) (NoSQL API) storage provider for
[YesSql](https://github.com/sebastienros/yessql) — the document-database layer used by
[Orchard Core](https://orchardcore.net/).

> **Status: working core, ~83% of YesSql's own conformance suite passing (207/249).**
> Document CRUD, map indexes, the common query surface, and reduce-index save+query work end-to-end
> against the Cosmos emulator. Reduce-index *lifecycle* (merge/update/delete), multi-index joins, and
> transaction rollback are not yet implemented (some are bounded by Cosmos itself). See
> [`docs/CONFORMANCE.md`](docs/CONFORMANCE.md) for the full matrix and
> [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for how it works.

## Why

YesSql ships first-party providers for SQL Server, PostgreSQL, MySQL, and SQLite only — all
relational. This project closes the loop so YesSql (and therefore Orchard Core and any YesSql-based
domain store) can run on Cosmos DB, enabling a single-Cosmos deployment topology.

## Approach

This is a **standalone NuGet package** that depends on YesSql — **not a fork**. YesSql persists
through an ADO.NET `DbConnection` (from `IConnectionFactory`) driven by SQL from `ISqlDialect`, so the
provider supplies a co-designed pair:

- a **Cosmos-backed ADO.NET shim** (`DbConnection`/`DbCommand`/`DbDataReader`/`DbTransaction`), and
- an **`ISqlDialect`** that emits a constrained SQL surface the shim translates into Cosmos SDK
  operations.

Documents and index rows live as type-discriminated items in a single container, partitioned by their
source table name. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Usage

```csharp
using YesSql;
using YesSql.Provider.CosmosDb;
using Microsoft.Azure.Cosmos;

var configuration = new Configuration()
    .UseCosmosDb(new CosmosDbOptions
    {
        AccountEndpoint = "https://my-account.documents.azure.com:443/",
        AccountKey      = "<key>",
        DatabaseId      = "myapp",
        ContainerId     = "yessql",      // default
        PartitionKeyPath = "/pk",        // default
        // ClientOptions = ...           // only needed for the emulator (see below)
    })
    .UseDefaultIdGenerator();

var store = await StoreFactory.CreateAndInitializeAsync(configuration);

await using var session = store.CreateSession();
await session.SaveAsync(new Person { Name = "Alice" });
await session.SaveChangesAsync();
```

### Local emulator

The provider is developed against the **Azure Cosmos DB Linux emulator (vnext preview)**. Two gotchas:

- The vnext emulator gateway serves **HTTP on `:8081`, not HTTPS** — use `http://localhost:8081/`.
- Use `ConnectionMode.Gateway` + `LimitToEndpoint = true`, and accept the self-signed cert.

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

## Building and testing

```bash
dotnet build YesSql.Provider.CosmosDb.slnx

# Hand-written provider tests (need the emulator running)
dotnet test test/YesSql.Provider.CosmosDb.Tests

# YesSql's own conformance suite against Cosmos (see docs/CONFORMANCE.md)
dotnet test test/Conformance/YesSql.Provider.CosmosDb.Conformance.csproj
```

## Targets

`net8.0;net10.0` — matching YesSql 5.4.7.

## License

MIT
