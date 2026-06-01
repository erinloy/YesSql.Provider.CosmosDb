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
/// Verifies the PerStore single-partition atomic rollback: a unit of work whose changes are flushed
/// (eagerly written) but not committed is fully reverted, while a committed one persists.
/// </summary>
public class RollbackTests
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

    private static IStore? _store;

    private static async Task<IStore> GetStoreAsync()
    {
        if (_store != null)
        {
            return _store;
        }

        var configuration = new Configuration().UseCosmosDb(new CosmosDbOptions
        {
            AccountEndpoint = "http://localhost:8081/",
            AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
            DatabaseId = "yessql_rollback",
            PartitionStrategy = PartitionStrategy.PerStore,
            PartitionScope = "store",
            ClientOptions = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                }),
            },
        }).UseDefaultIdGenerator();

        var store = await StoreFactory.CreateAndInitializeAsync(configuration);
        store.RegisterIndexes<PersonIndexProvider>();
        return _store = store;
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_is_rolled_back()
    {
        var store = await GetStoreAsync();
        var name = "Rollback_" + Guid.NewGuid().ToString("N")[..8];

        long id;
        await using (var session = store.CreateSession())
        {
            var person = new Person { Firstname = name };
            await session.SaveAsync(person);
            id = person.Id;

            // Force an autoflush (eager write to Cosmos + undo recorded), then DO NOT save changes.
            var seen = await session.Query<Person, PersonByName>().Where(x => x.SomeName == name).CountAsync();
            Assert.Equal(1, seen); // visible within the session (read-your-writes)
        }

        // Disposed without SaveChangesAsync → rolled back.
        await using (var session = store.CreateSession())
        {
            Assert.Null(await session.GetAsync<Person>(id));
            Assert.Equal(0, await session.Query<Person, PersonByName>().Where(x => x.SomeName == name).CountAsync());
        }
    }

    [Fact]
    public async Task Committed_unit_of_work_persists()
    {
        var store = await GetStoreAsync();
        var name = "Commit_" + Guid.NewGuid().ToString("N")[..8];

        long id;
        await using (var session = store.CreateSession())
        {
            var person = new Person { Firstname = name };
            await session.SaveAsync(person);
            id = person.Id;
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            var loaded = await session.GetAsync<Person>(id);
            Assert.NotNull(loaded);
            Assert.Equal(1, await session.Query<Person, PersonByName>().Where(x => x.SomeName == name).CountAsync());
        }
    }
}
