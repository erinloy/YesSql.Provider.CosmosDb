using System;
using System.Data.Common;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
#if YESSQL6
using Xunit;
#else
using Xunit.Abstractions;
#endif
using YesSql.Provider.CosmosDb;

namespace YesSql.Tests
{
    /// <summary>
    /// Runs YesSql's full CoreTests conformance suite against the Cosmos DB provider. It targets the emulator at
    /// http://localhost:8081/ unless COSMOS_TEST_ENDPOINT and COSMOS_TEST_KEY name an account, which is how the suite
    /// is run against a real one. The run creates one database, named yessql_conf_*, and does not delete it.
    /// </summary>
    public class CosmosTests : CoreTests
    {
        private static readonly string Endpoint =
            Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") ?? "http://localhost:8081/";

        // Without an explicit account the key is Microsoft's published, well-known emulator key (not a secret). An
        // account named by COSMOS_TEST_ENDPOINT needs its own key in COSMOS_TEST_KEY; the emulator key is never sent to it.
        private static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is null
            ? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
            : Environment.GetEnvironmentVariable("COSMOS_TEST_KEY")
                ?? throw new InvalidOperationException("COSMOS_TEST_ENDPOINT is set, so COSMOS_TEST_KEY is required.");

        private static bool IsEmulator => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
        private const string ContainerId = "yessql";

        // One database for the whole run (CoreTests caches _configuration statically).
        private static readonly string DatabaseId = "yessql_conf_" + Guid.NewGuid().ToString("N")[..8];

        public CosmosTests(ITestOutputHelper output) : base(output)
        {
        }

        private static CosmosClientOptions ClientOptions() => new()
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            // Skip certificate validation for a loopback emulator only; a real account must be validated.
            HttpClientFactory = IsEmulator
                ? () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                })
                : null,
        };

        // COSMOS_ID_GENERATOR=Block runs the suite with the block id generator, which leases ids from the store, as YesSql's
        // own provider suites do. Without it the suite uses the default generator, which seeds a counter from MAX(Id).
        private static readonly bool UseBlockIds =
            string.Equals(Environment.GetEnvironmentVariable("COSMOS_ID_GENERATOR"), "Block", StringComparison.OrdinalIgnoreCase);

        private static readonly PartitionStrategy Strategy =
            string.Equals(Environment.GetEnvironmentVariable("COSMOS_PARTITION"), "PerStore", StringComparison.OrdinalIgnoreCase)
                ? PartitionStrategy.PerStore
                : PartitionStrategy.PerTable;

        // The vnext emulator reports Gateway=OK before its pgcosmos extension finishes starting, so the first
        // queries race it and fail with 503 "pgcosmos extension is still starting". Block once until a real
        // round-trip succeeds, so the suite self-warms instead of flaking.
        private static readonly object WarmupLock = new();
        private static bool _warmed;

        private static void EnsureEmulatorWarm()
        {
            lock (WarmupLock)
            {
                if (_warmed || !IsEmulator)
                {
                    return;
                }

                using var client = new CosmosClient(Endpoint, Key, ClientOptions());
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    try
                    {
                        var db = client.CreateDatabaseIfNotExistsAsync("warmup_gate").GetAwaiter().GetResult().Database;
                        var container = db.CreateContainerIfNotExistsAsync("warmup", "/pk").GetAwaiter().GetResult().Container;
                        container.UpsertItemAsync(new { id = "w", pk = "w" }, new PartitionKey("w")).GetAwaiter().GetResult();
                        container.ReadItemAsync<object>("w", new PartitionKey("w")).GetAwaiter().GetResult();
                        _warmed = true;
                        return;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(2000);
                    }
                }

                throw new InvalidOperationException("Cosmos emulator did not become ready within the warmup window.");
            }
        }

        protected override IConfiguration CreateConfiguration()
        {
            EnsureEmulatorWarm();
            var configuration = new Configuration()
                .UseCosmosDb(new CosmosDbOptions
                {
                    AccountEndpoint = Endpoint,
                    AccountKey = Key,
                    DatabaseId = DatabaseId,
                    ContainerId = ContainerId,
                    ClientOptions = ClientOptions(),
                    PartitionStrategy = Strategy,
                    PartitionScope = "conf",
                })
                .SetTablePrefix(TablePrefix);
#if YESSQL6
            configuration = configuration.WithThreadSafetyChecks();
#endif
            return (UseBlockIds ? configuration.UseBlockIdGenerator() : configuration.UseDefaultIdGenerator())
                .SetIdentityColumnSize(IdentityColumnSize.Int64);
        }

        // DDL is a no-op on a schemaless store; nothing to clean.
        protected override Task CleanDatabaseAsync(IConfiguration configuration, bool throwOnError)
            => Task.CompletedTask;

        // Per-test isolation: delete every item in the container instead of raw DELETE FROM <table>. The rows of the block id
        // generator's Identifiers table stay, as they do in YesSql's own provider suites: CoreTests shares one configuration, so
        // one generator that has already leased its collections for the whole run.
        protected override async Task ClearTablesAsync(IConfiguration configuration)
        {
            using var client = new CosmosClient(Endpoint, Key, ClientOptions());
            var container = client.GetContainer(DatabaseId, ContainerId);

            try
            {
                using var iterator = container.GetItemQueryIterator<JObject>("SELECT c.id, c.pk, c.__table FROM c");
                while (iterator.HasMoreResults)
                {
                    foreach (var item in await iterator.ReadNextAsync())
                    {
                        if (item["__table"]?.ToString().EndsWith("Identifiers", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            continue;
                        }

                        var id = item["id"]!.ToString();
                        var pk = item["pk"]?.ToString() ?? id;
                        await container.DeleteItemAsync<JObject>(id, new PartitionKey(pk));
                    }
                }
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // container not created yet — nothing to clear
            }
        }
    }
}
