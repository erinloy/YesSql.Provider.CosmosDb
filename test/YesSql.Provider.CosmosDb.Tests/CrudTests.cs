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

public class CrudTests
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

    private static CosmosDbOptions Options() => new()
    {
        AccountEndpoint = "http://localhost:8081/",
        AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
        DatabaseId = "yessql_crud_" + Guid.NewGuid().ToString("N")[..8],
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
    public async Task Update_modifies_document()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options()));

        long id;
        await using (var s = store.CreateSession())
        {
            var p = new Person { Firstname = "Bob" };
            await s.SaveAsync(p);
            await s.SaveChangesAsync();
            id = p.Id;
        }

        await using (var s = store.CreateSession())
        {
            var p = await s.GetAsync<Person>(id);
            p!.Firstname = "Bobby";
            await s.SaveAsync(p);
            await s.SaveChangesAsync();
        }

        await using (var s = store.CreateSession())
        {
            var p = await s.GetAsync<Person>(id);
            Assert.Equal("Bobby", p!.Firstname);
        }
    }

    [Fact]
    public async Task Delete_removes_document()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options()));

        long id;
        await using (var s = store.CreateSession())
        {
            var p = new Person { Firstname = "Carol" };
            await s.SaveAsync(p);
            await s.SaveChangesAsync();
            id = p.Id;
        }

        await using (var s = store.CreateSession())
        {
            var p = await s.GetAsync<Person>(id);
            s.Delete(p!);
            await s.SaveChangesAsync();
        }

        await using (var s = store.CreateSession())
        {
            Assert.Null(await s.GetAsync<Person>(id));
        }
    }

    [Fact]
    public async Task Query_returns_multiple_matches()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options()));
        store.RegisterIndexes<PersonIndexProvider>();

        await using (var s = store.CreateSession())
        {
            await s.SaveAsync(new Person { Firstname = "Dup" });
            await s.SaveAsync(new Person { Firstname = "Dup" });
            await s.SaveAsync(new Person { Firstname = "Unique" });
            await s.SaveChangesAsync();
        }

        await using (var s = store.CreateSession())
        {
            var dups = (await s.Query<Person, PersonByName>().Where(x => x.SomeName == "Dup").ListAsync()).ToList();
            Assert.Equal(2, dups.Count);
            Assert.All(dups, p => Assert.Equal("Dup", p.Firstname));
        }
    }
}
