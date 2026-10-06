# Orchard Core integration

Orchard Core stores its data through YesSql, so it can use this provider. Orchard does not yet have Cosmos DB as a built-in database option, so the provider has to be wired in by replacing the tenant's `IStore` registration.

[`samples/OrchardSmokeTest`](../samples/OrchardSmokeTest) is a minimal Orchard Core host (net10.0) that does this. It builds against Orchard Core 3.0.1 (YesSql 5.4.7) by default, and against the 4.0 preview (YesSql 6.0.0) with `-p:OrchardCoreVersion=4.0.0-preview-19175`. Against the Cosmos emulator it runs the `Headless` setup recipe, creates the tenant, and serves the site and the admin login page. No SQLite file is created; every document and index row goes to Cosmos.

The sample uses the default `PerTable` partition strategy. Orchard has not been tested here with `PerStore`.

## How Orchard selects a database

`AddDataAccess()` in `OrchardCore.Data.YesSql` registers a singleton `IStore` per shell. It reads the shell's `DatabaseProvider` setting and calls the matching YesSql extension (`UseSqlServer`, `UseSqLite`, `UseMySql`, `UsePostgreSql`), then registers the shell's `IIndexProvider`s with the store. The set of providers is fixed in three places: the `DatabaseProviderValue` constants, the `switch` in `AddDataAccess`, and the `switch` in `DbConnectionValidator` used during setup. None of them is extensible from outside Orchard.

Orchard also ties YesSql to the request. A scoped `ISession` is committed through `IDocumentStore.CommitAsync()` when the request scope is disposed, and `IDocumentStore.CancelAsync()` is called when the request throws. With `PerStore` the provider reverts an unsaved unit of work with transactional batches, which is atomic for a request that changed up to 100 items. With `PerTable` the rollback is best effort. Neither isolates the request from other sessions while it runs. See [PARTITIONING.md](PARTITIONING.md) for the limits.

## Wiring it in

Register an `IStore` after `AddOrchardCms()` so that it replaces Orchard's own. The registration mirrors Orchard's `GetStoreConfiguration` and calls `UseCosmosDb`:

```csharp
builder.Services
    .AddOrchardCms()
    .AddSetupFeatures("OrchardCore.AutoSetup")
    .ConfigureServices(services =>
    {
        services.AddSingleton<IStore>(sp =>
        {
            var shellSettings = sp.GetRequiredService<ShellSettings>();
            if (shellSettings.IsUninitialized() || shellSettings["DatabaseProvider"] is null)
            {
                return null;
            }

            var serializerOptions = sp.GetRequiredService<IOptions<DocumentJsonSerializerOptions>>();

            var configuration = new YesSql.Configuration
            {
                IdentityColumnSize = IdentityColumnSize.Int64,
                Logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("YesSql"),
                ContentSerializer = new DefaultContentJsonSerializer(serializerOptions.Value.SerializerOptions),
            };

            configuration
                .UseCosmosDb(new CosmosDbOptions { /* endpoint, key, database */ })
                .UseDefaultIdGenerator();

            var tablePrefix = shellSettings["TablePrefix"];
            if (!string.IsNullOrWhiteSpace(tablePrefix))
            {
                configuration.SetTablePrefix(tablePrefix.Trim() + "_");
            }

            var store = StoreFactory.Create(configuration);
            store.RegisterIndexes(sp.GetServices<IIndexProvider>());
            return store;
        });
    });
```

The complete working version is [`samples/OrchardSmokeTest/Program.cs`](../samples/OrchardSmokeTest/Program.cs).

Setup validates a database provider and connection string before it commits. The sample avoids the interactive setup screen by using the `OrchardCore.AutoSetup` feature, and declares `"DatabaseProvider": "Sqlite"` in the tenant settings only so that Orchard's validation passes. The label is never used to open a connection, because the replacement `IStore` handles all data access.

Three details are easy to get wrong:

1. In `appsettings.json`, `OrchardCore_AutoSetup` must sit under the `OrchardCore` section. At the root of the file it is ignored without any message.
2. Enable the feature on the setup shell with `.AddSetupFeatures("OrchardCore.AutoSetup")`.
3. Register the `IStore` after `AddOrchardCms()` so that it is the registration Orchard resolves.

## Running the sample

Start the emulator (see the [README](../README.md#running-against-the-emulator)), then:

```bash
dotnet run --project samples/OrchardSmokeTest
```

To run it on the Orchard Core 4.0 preview (YesSql 6.0.0), select the version at build time:

```bash
dotnet run --project samples/OrchardSmokeTest -p:OrchardCoreVersion=4.0.0-preview-19175
```

`samples/nuget.config` adds Orchard Core's preview feed for the samples only. `scripts/smoke-sample.sh <version>` builds the sample, starts it against a running emulator with a fresh database, and checks that `/`, `/Login` and `/admin` return the site. CI runs it for both versions.

The `Cosmos` section of `appsettings.json` holds the endpoint, key and database name. On first start AutoSetup creates the `Default` tenant and its data in the `orchard_smoke` database.

## Not covered yet

- Creating, editing, publishing and deleting content items, and listing and filtering them, have not been exercised through the Orchard UI on this provider.
- Request rollback through `CancelAsync` is covered by the provider's rollback tests and YesSql's conformance suite, but not through an Orchard request that fails.

## Making it a built-in option

The cleaner long-term route is an upstream change to Orchard Core: add a `CosmosDb` value to `DatabaseProviderValue`, a case in both `switch` statements, and a data provider entry for the setup screen. That would make Orchard depend on this package, so it would need agreement from the Orchard maintainers.

Reference: [Orchard Core source](https://github.com/OrchardCMS/OrchardCore), `src/OrchardCore/OrchardCore.Data.YesSql`.
