using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Xunit;
using YesSql;
using YesSql.Provider.CosmosDb;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Live round-trip against the Azure Cosmos DB Linux emulator
/// (mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview on https://localhost:8081).
/// </summary>
public class CosmosRoundTripTests
{
    private const string Endpoint = "http://localhost:8081/";

    // Well-known Cosmos emulator key.
    private const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public sealed class Person
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private static CosmosDbOptions EmulatorOptions() => new()
    {
        AccountEndpoint = Endpoint,
        AccountKey = Key,
        DatabaseId = "yessql_test_" + Guid.NewGuid().ToString("N")[..8],
        ClientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        },
    };

    [Fact]
    public async Task Can_save_and_load_document()
    {
        var configuration = new Configuration().UseCosmosDb(EmulatorOptions());
        var store = await StoreFactory.CreateAndInitializeAsync(configuration);

        long id;
        await using (var session = store.CreateSession())
        {
            var person = new Person { Name = "Alice" };
            await session.SaveAsync(person);
            await session.SaveChangesAsync();
            id = person.Id;
        }

        Assert.True(id > 0, "YesSql should have assigned a generated id.");

        await using (var session = store.CreateSession())
        {
            var loaded = await session.GetAsync<Person>(id);
            Assert.NotNull(loaded);
            Assert.Equal("Alice", loaded!.Name);
            Assert.Equal(id, loaded.Id);
        }
    }
}
