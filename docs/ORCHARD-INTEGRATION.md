# Running Orchard Core on this provider

> ## ✅ VALIDATED — Orchard Core boots and runs on this provider
> `samples/OrchardSmokeTest` is a minimal Orchard Core CMS host (OrchardCore 2.2.1, net8, YesSql 5.4.7)
> configured with the Cosmos `IStore` override + AutoSetup. On launch against the Cosmos emulator it
> **provisioned a full tenant via the setup recipe with zero errors**, then served the site
> (`<title>Cosmos Smoke Test</title>`, admin login HTTP 200). The Cosmos `orchard_smoke` database held
> 20 items spanning the whole data layer — `Document`s, map indexes (`UserIndex`,
> `OpenId_OpenIdScopeIndex`), a **reduce index + bridge** (`UserByRoleNameIndex` +
> `UserByRoleNameIndex_Document`), and the provider's `__seq` id counters. **No `OrchardCore.db`
> sqlite file was created** — the `Sqlite` provider label only satisfies setup validation; all data
> went to Cosmos.
>
> ### What made it work (3 things)
> 1. `OrchardCore_AutoSetup` config must be nested **under `"OrchardCore"`** in appsettings.json (the
>    shell config is rooted there) — at the JSON root it is silently ignored.
> 2. Enable the feature on the setup shell: `.AddOrchardCms().AddSetupFeatures("OrchardCore.AutoSetup")`.
> 3. Override the per-tenant `IStore` (registered last → wins) to call `UseCosmosDb`, declaring
>    `DatabaseProvider: "Sqlite"` in AutoSetup only so the connection validator passes.
>
> See `samples/OrchardSmokeTest/Program.cs` + `appsettings.json` for the working configuration.
> (Caveat unchanged: request-scoped rollback on error is not supported — Cosmos has no cross-partition ACID.)

---

Based on reading Orchard Core's source (`OrchardCore.Data.YesSql` / `OrchardCore.Data.Abstractions`,
cloned to `Z:\SOURCE\REFERENCE\libraries\orchardcore`).

## How Orchard Core wires up YesSql

All data access is set up in `OrchardCore.Data.YesSql/OrchardCoreBuilderExtensions.AddDataAccess()`:

1. **Provider registration (setup UI).** It calls `services.TryAddDataProvider(name, value, …)` once per
   database — Sql Server, Sqlite (default), MySql, Postgres. These populate the dropdown on the setup
   screen. The string values live in `DatabaseProviderValue` (`SqlConnection`, `Sqlite`, `MySql`,
   `Postgres`).

2. **Store construction.** It registers a singleton `IStore` factory that:
   - returns `null` if the shell is uninitialized or has no `DatabaseProvider` (pre-setup);
   - builds a `YesSql.Configuration` (`GetStoreConfiguration`: table-name convention, content
     serializer, `IdentityColumnSize`, logger, isolation level);
   - **`switch (shellSettings["DatabaseProvider"])`** → calls the YesSql provider extension:
     ```
     SqlConnection → storeConfiguration.UseSqlServer(conn, isolation, schema).UseBlockIdGenerator()
     Sqlite        → storeConfiguration.UseSqLite(conn, isolation).UseDefaultIdGenerator()
     MySql         → storeConfiguration.UseMySql(conn, isolation, schema).UseBlockIdGenerator()
     Postgres      → storeConfiguration.UsePostgreSql(conn, isolation, schema).UseBlockIdGenerator()
     default       → throw
     ```
   - sets the table prefix, then `StoreFactory.Create(storeConfiguration)` and
     `store.RegisterIndexes(indexes)` (all registered `IIndexProvider`s).

3. **Connection validation (setup).** `DbConnectionValidator` has its **own** `switch` mapping each
   provider to a `(IConnectionFactory, ISqlDialect)` — used to test the connection string before setup
   commits.

4. **Session + transaction lifecycle.** A scoped `ISession` is created from `store.CreateSession()`. On
   the shell scope, Orchard registers `IDocumentStore.CommitAsync()` on **before-dispose** (success) and
   `IDocumentStore.CancelAsync()` on **exception**. So each request commits at the end, or *cancels
   (rolls back)* on error.

   > **This is where our Cosmos limitation lands.** `CancelAsync()` expects the session's writes to be
   > undone. Cosmos has no cross-partition transaction, and this provider writes eagerly, so a failed
   > Orchard request will **not** roll back partial writes. Most requests commit successfully, but error
   > paths can leave partial data. (Conformance: `NoSavingChangesShouldRollbackAutoFlush`, etc.)

## Where Cosmos has to plug in

Orchard's provider selection is **three hardcoded switches** (`DatabaseProviderValue`,
`AddDataAccess`, `DbConnectionValidator`) plus the `TryAddDataProvider` list — none are extensible
points, so there are two ways in:

### Option A — override the `IStore` singleton (no fork; recommended for the smoke test)
After `AddOrchardCore()`, register our own `IStore` last (last registration wins for `GetService`):

```csharp
services.AddSingleton<IStore>(sp =>
{
    var config = /* mirror GetStoreConfiguration: TableNameConvention, ContentSerializer, … */
        new YesSql.Configuration { /* … */ }
        .UseCosmosDb(new CosmosDbOptions { AccountEndpoint = …, AccountKey = …, DatabaseId = … })
        .UseDefaultIdGenerator();
    var store = StoreFactory.Create(config);
    store.RegisterIndexes(sp.GetServices<IIndexProvider>());
    return store;
});
```
Combine with **AutoSetup** (`OrchardCore.AutoSetup`) so the interactive setup screen (and
`DbConnectionValidator`) is skipped, and register a permissive `IDbConnectionValidator` if needed.

### Option B — first-class provider (upstream contribution)
Add a `CosmosDb` constant to `DatabaseProviderValue`, a `case` to both switches, and a
`TryAddDataProvider(name: "Cosmos DB", value: DatabaseProviderValue.CosmosDb, …)`. Cleanest long-term,
but makes Orchard depend on the Cosmos provider package; best done as a PR or a small Orchard module.

## Smoke-test plan

Steps 1–2 are **done and validated** (see the banner above — `samples/OrchardSmokeTest` boots a tenant
via AutoSetup on Cosmos and serves the site). Steps 3–4 remain as deeper manual exercises, not yet run:

1. ✅ Minimal ASP.NET Core host + `AddOrchardCore().AddDataAccess()` (or the `OrchardCore.Application.*`
   meta-package) targeting the Cosmos emulator.
2. ✅ Use Option A to force `UseCosmosDb`, AutoSetup to provision a tenant.
3. ⬜ Exercise: create a content type, create/edit/publish/delete content items, list and filter them.
4. ⬜ Watch the request-rollback path (intentional failure) to confirm the documented limitation
   (`PerTable` best-effort vs `PerStore` atomic).
