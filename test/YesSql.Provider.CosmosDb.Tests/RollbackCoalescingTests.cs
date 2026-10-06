using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Rollback with PerStore when a unit of work writes the same items repeatedly or touches more items than fit in
/// one transactional batch (100 operations).
/// </summary>
public class RollbackCoalescingTests
{
    public class Person
    {
        public int Id { get; set; }
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

    private static async Task<IStore> NewStoreAsync()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_rbcoal"), strategy: PartitionStrategy.PerStore)));
        store.RegisterIndexes<PersonIndexProvider>();
        return store;
    }

    [Fact]
    public async Task Repeated_writes_to_the_same_items_are_rolled_back_to_the_committed_state()
    {
        var store = await NewStoreAsync();
        const int count = 20;

        var ids = new List<int>();
        await using (var session = store.CreateSession())
        {
            for (var i = 0; i < count; i++)
            {
                var person = new Person { Name = $"original-{i}" };
                await session.SaveAsync(person);
                ids.Add(person.Id);
            }

            await session.SaveChangesAsync();
        }

        // Rewrite every document several times, flushing between rounds so each round is written and recorded,
        // then delete one and add another. The session is disposed without saving, so all of it is rolled back.
        await using (var session = store.CreateSession())
        {
            for (var round = 0; round < 8; round++)
            {
                for (var i = 0; i < count; i++)
                {
                    var person = (await session.GetAsync<Person>(ids[i]))!;
                    person.Name = $"round-{round}-{i}";
                    await session.SaveAsync(person);
                }

                await session.Query<Person>().CountAsync(); // autoflush
            }

            session.Delete((await session.GetAsync<Person>(ids[0]))!);
            await session.SaveAsync(new Person { Name = "added-in-the-unit-of-work" });
            await session.Query<Person>().CountAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(count, await session.Query<Person>().CountAsync());
            for (var i = 0; i < count; i++)
            {
                Assert.Equal($"original-{i}", (await session.GetAsync<Person>(ids[i]))!.Name);
                Assert.Equal(1, await session.Query<Person, PersonByName>(x => x.Name == $"original-{i}").CountAsync());
            }

            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name == "added-in-the-unit-of-work").CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name.StartsWith("round-")).CountAsync());
        }
    }

    [Fact]
    public async Task A_unit_of_work_larger_than_one_batch_is_rolled_back_completely()
    {
        var store = await NewStoreAsync();

        await using (var session = store.CreateSession())
        {
            // 130 documents and 130 index rows: more than the 100 operations a transactional batch holds.
            for (var i = 0; i < 130; i++)
            {
                await session.SaveAsync(new Person { Name = $"bulk-{i}" });
            }

            Assert.Equal(130, await session.Query<Person>().CountAsync()); // autoflush
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(0, await session.Query<Person>().CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name.StartsWith("bulk-")).CountAsync());
        }
    }
}
