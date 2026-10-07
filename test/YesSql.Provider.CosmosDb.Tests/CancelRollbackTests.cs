using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// <c>ISession.CancelAsync</c>, and YesSql's own handling of a failed read or query, release the transaction by
/// disposing it without calling <c>Rollback</c>. ADO.NET expects an uncommitted transaction to be rolled back when
/// it is disposed, so writes that were already sent to Cosmos have to be undone.
/// </summary>
public class CancelRollbackTests
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

    private static async Task<IStore> NewStoreAsync(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_cancel"), strategy: strategy)));
        store.RegisterIndexes<PersonIndexProvider>();
        return store;
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task CancelAsync_undoes_writes_that_were_already_flushed(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        int keptId;
        await using (var session = store.CreateSession())
        {
            var kept = new Person { Name = "kept" };
            await session.SaveAsync(kept);
            await session.SaveChangesAsync();
            keptId = kept.Id;
        }

        await using (var session = store.CreateSession())
        {
            var kept = (await session.GetAsync<Person>(keptId))!;
            kept.Name = "renamed";
            await session.SaveAsync(kept);
            await session.SaveAsync(new Person { Name = "added" });
            await session.Query<Person>().CountAsync(); // autoflush: the writes are now in Cosmos

            await session.CancelAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(1, await session.Query<Person>().CountAsync());
            Assert.Equal("kept", (await session.GetAsync<Person>(keptId))!.Name);
            Assert.Equal(1, await session.Query<Person, PersonByName>(x => x.Name == "kept").CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name == "renamed").CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name == "added").CountAsync());
        }
    }

    [Fact]
    public async Task A_committed_unit_of_work_is_not_undone_when_the_session_is_disposed()
    {
        var store = await NewStoreAsync(PartitionStrategy.PerStore);

        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "committed" });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(1, await session.Query<Person, PersonByName>(x => x.Name == "committed").CountAsync());
        }
    }
}
