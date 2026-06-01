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

public class QueryFeaturesTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Firstname { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    public class PersonIndex : MapIndex
    {
        public long DocumentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
            => context.For<PersonIndex>().Map(p => new PersonIndex { Name = p.Firstname, Age = p.Age });
    }

    private static CosmosDbOptions Options() => new()
    {
        AccountEndpoint = "http://localhost:8081/",
        AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
        DatabaseId = "yessql_qf_" + Guid.NewGuid().ToString("N")[..8],
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

    private static async Task<IStore> SeedAsync(params (string name, int age)[] people)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options()));
        store.RegisterIndexes<PersonIndexProvider>();
        await using var s = store.CreateSession();
        foreach (var (name, age) in people)
        {
            await s.SaveAsync(new Person { Firstname = name, Age = age });
        }

        await s.SaveChangesAsync();
        return store;
    }

    [Fact]
    public async Task CountAsync_counts_matches()
    {
        var store = await SeedAsync(("X", 1), ("X", 2), ("Y", 3));
        await using var s = store.CreateSession();
        var count = await s.Query<Person, PersonIndex>().Where(x => x.Name == "X").CountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Range_query_filters()
    {
        var store = await SeedAsync(("A", 20), ("B", 30), ("C", 40));
        await using var s = store.CreateSession();
        var over25 = (await s.Query<Person, PersonIndex>().Where(x => x.Age > 25).ListAsync()).ToList();
        Assert.Equal(2, over25.Count);
    }

    [Fact]
    public async Task OrderBy_sorts_results()
    {
        var store = await SeedAsync(("Charlie", 1), ("Alice", 2), ("Bob", 3));
        await using var s = store.CreateSession();
        var names = (await s.Query<Person, PersonIndex>().OrderBy(x => x.Name).ListAsync())
            .Select(p => p.Firstname).ToList();
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, names);
    }
}
