using System;
using System.Data.Common;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using YesSql.Provider.CosmosDb;

namespace YesSql.Tests
{
    /// <summary>
    /// Runs YesSql's full CoreTests conformance suite against the Cosmos DB provider (emulator).
    /// </summary>
    public class CosmosTests : CoreTests
    {
        private const string Endpoint = "http://localhost:8081/";
        private const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
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
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        };

        private static readonly PartitionStrategy Strategy =
            string.Equals(Environment.GetEnvironmentVariable("COSMOS_PARTITION"), "PerStore", StringComparison.OrdinalIgnoreCase)
                ? PartitionStrategy.PerStore
                : PartitionStrategy.PerTable;

        protected override IConfiguration CreateConfiguration()
            => new Configuration()
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
                .SetTablePrefix(TablePrefix)
                .UseDefaultIdGenerator()
                .SetIdentityColumnSize(IdentityColumnSize.Int64);

        // DDL is a no-op on a schemaless store; nothing to clean.
        protected override Task CleanDatabaseAsync(IConfiguration configuration, bool throwOnError)
            => Task.CompletedTask;

        // Per-test isolation: delete every item in the container instead of raw DELETE FROM <table>.
        protected override async Task ClearTablesAsync(IConfiguration configuration)
        {
            using var client = new CosmosClient(Endpoint, Key, ClientOptions());
            var container = client.GetContainer(DatabaseId, ContainerId);

            try
            {
                using var iterator = container.GetItemQueryIterator<JObject>("SELECT c.id, c.pk FROM c");
                while (iterator.HasMoreResults)
                {
                    foreach (var item in await iterator.ReadNextAsync())
                    {
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
