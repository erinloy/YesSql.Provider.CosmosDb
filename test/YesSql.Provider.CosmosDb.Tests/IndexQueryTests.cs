using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Xunit;
using YesSql;
using YesSql.Indexes;
using YesSql.Provider.CosmosDb;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Exercises a map index + query through the provider. Initially used to capture the exact SQL YesSql
/// emits for index writes and index-joined queries, then to verify the translation.
/// </summary>
public class IndexQueryTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Firstname { get; set; } = string.Empty;
    }

    public class PersonByName : MapIndex
    {
        public long DocumentId { get; set; }
        public string SomeName { get; set; } = string.Empty;
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
            => context.For<PersonByName>().Map(p => new PersonByName { SomeName = p.Firstname });
    }

    private static CosmosDbOptions EmulatorOptions() => new()
    {
        AccountEndpoint = "http://localhost:8081/",
        AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
        DatabaseId = "yessql_idx_" + Guid.NewGuid().ToString("N")[..8],
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
    public async Task Can_query_by_map_index()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(EmulatorOptions()));
        store.RegisterIndexes<PersonIndexProvider>();

        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Firstname = "Alice" });
            await session.SaveAsync(new Person { Firstname = "Bob" });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            var alice = await session.Query<Person, PersonByName>().Where(x => x.SomeName == "Alice").FirstOrDefaultAsync();
            Assert.NotNull(alice);
            Assert.Equal("Alice", alice!.Firstname);
        }
    }
}
