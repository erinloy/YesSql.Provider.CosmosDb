# YesSql.Provider.CosmosDb

An [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) (NoSQL API) storage provider for
[YesSql](https://github.com/sebastienros/yessql) — the document-database layer used by
[Orchard Core](https://orchardcore.net/).

> **Status: early spike.** Not yet usable. See `SPIKE-NOTES.md` for the architecture investigation.

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
  operations (documents in a single container, index properties embedded and served by Cosmos
  automatic indexing, atomicity via partition-scoped stored procedures).

Patterns are adapted from the `imranmomin/Hangfire.AzureCosmosDb` provider.

## Targets

`net8.0;net10.0` — matching YesSql. Built and tested against the Azure Cosmos DB Linux emulator.

## License

MIT
