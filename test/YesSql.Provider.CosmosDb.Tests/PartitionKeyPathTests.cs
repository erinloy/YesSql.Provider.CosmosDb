using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

public class PartitionKeyPathTests
{
    public class Person
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class PersonByName : MapIndex
    {
        public string Name { get; set; } = string.Empty;
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
            => context.For<PersonByName>().Map(p => new PersonByName { Name = p.Name });
    }

    [Theory]
    [InlineData("")]
    [InlineData("pk")]
    [InlineData("/")]
    [InlineData("/a/b")]
    [InlineData("/has-dash")]
    [InlineData("/id")]
    public void Unsupported_paths_are_rejected_when_the_provider_is_configured(string path)
    {
        Assert.Throws<ArgumentException>(() => new Configuration().UseCosmosDb(Emulator.Options("unused", partitionKeyPath: path)));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_custom_partition_key_path_is_used_for_every_item(PartitionStrategy strategy)
    {
        var databaseId = Emulator.NewDatabaseId("yessql_pkpath");
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(databaseId, "/tenantId", strategy)));
        store.RegisterIndexes<PersonIndexProvider>();

        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "Alice" });
            await session.SaveAsync(new Person { Name = "Bob" });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            var found = await session.Query<Person, PersonByName>(x => x.Name == "Bob").ListAsync();
            Assert.Equal(["Bob"], found.Select(p => p.Name).ToArray());
            Assert.Equal(2, await session.Query<Person>().CountAsync());
        }

        using var client = Emulator.NewClient();
        var container = client.GetContainer(databaseId, "yessql");
        Assert.Equal("/tenantId", (await container.ReadContainerAsync()).Resource.PartitionKeyPath);

        var items = new List<JObject>();
        using var iterator = container.GetItemQueryIterator<JObject>("SELECT * FROM c");
        while (iterator.HasMoreResults)
        {
            items.AddRange(await iterator.ReadNextAsync());
        }

        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.NotNull(item["tenantId"]);
            Assert.Null(item["pk"]);
        });
    }

    private static async Task CreateContainerAsync(string databaseId, string partitionKeyPath)
    {
        using var client = Emulator.NewClient();
        var database = (await client.CreateDatabaseIfNotExistsAsync(databaseId)).Database;
        await database.CreateContainerIfNotExistsAsync("yessql", partitionKeyPath);
    }

    [Fact]
    public async Task An_existing_container_with_a_different_path_is_rejected_when_creating()
    {
        var databaseId = Emulator.NewDatabaseId("yessql_pkmismatch");
        await CreateContainerAsync(databaseId, "/pk");

        // The Cosmos SDK reports the mismatch when asked to create the container if it does not exist.
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Emulator.Options(databaseId, "/tenantId"))));

        Assert.Contains("/pk", exception.Message);
        Assert.Contains("/tenantId", exception.Message);
    }

    [Fact]
    public async Task An_existing_container_with_a_different_path_is_rejected_when_not_creating()
    {
        var databaseId = Emulator.NewDatabaseId("yessql_pkmismatch2");
        await CreateContainerAsync(databaseId, "/pk");

        // With creation disabled the provider reads the container itself and compares the paths.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StoreFactory.CreateAndInitializeAsync(
                new Configuration().UseCosmosDb(Emulator.Options(databaseId, "/tenantId", createIfNotExists: false))));

        Assert.Contains("/pk", exception.Message);
        Assert.Contains("/tenantId", exception.Message);
    }

    [Fact]
    public async Task A_missing_container_fails_when_creation_is_disabled()
    {
        var databaseId = Emulator.NewDatabaseId("yessql_nocreate");

        await Assert.ThrowsAnyAsync<CosmosException>(
            () => StoreFactory.CreateAndInitializeAsync(
                new Configuration().UseCosmosDb(Emulator.Options(databaseId, createIfNotExists: false))));
    }

    [Fact]
    public async Task An_existing_container_can_be_used_when_creation_is_disabled()
    {
        var databaseId = Emulator.NewDatabaseId("yessql_existing");

        await CreateContainerAsync(databaseId, "/customKey");

        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(databaseId, "/customKey", createIfNotExists: false)));

        long id;
        await using (var session = store.CreateSession())
        {
            var person = new Person { Name = "Carol" };
            await session.SaveAsync(person);
            await session.SaveChangesAsync();
            id = person.Id;
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal("Carol", (await session.GetAsync<Person>(id))!.Name);
        }
    }
}
