using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Ordering and paging of documents that are loaded through an index. A page of documents is read with several
/// concurrent point reads, and the result must keep the requested order.
/// </summary>
public class PagingTests
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

    private const int Total = 40;

    private static async Task<IStore> NewStoreWithPeopleAsync()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_paging"))));
        store.RegisterIndexes<PersonIndexProvider>();

        await using var session = store.CreateSession();

        // Insert out of order so the order of the result cannot come from insertion order.
        foreach (var i in Enumerable.Range(0, Total).OrderBy(i => (i * 17) % Total))
        {
            await session.SaveAsync(new Person { Name = $"n{i:00}" });
        }

        await session.SaveChangesAsync();
        return store;
    }

    private static string[] Names(int from, int count) =>
        Enumerable.Range(from, count).Select(i => $"n{i:00}").ToArray();

    [Fact]
    public async Task A_page_of_an_ordered_query_keeps_its_order()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var page = await session.Query<Person, PersonByName>().OrderBy(x => x.Name).Skip(5).Take(20).ListAsync();

        Assert.Equal(Names(5, 20), page.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_descending_page_keeps_its_order()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var page = await session.Query<Person, PersonByName>().OrderByDescending(x => x.Name).Skip(3).Take(15).ListAsync();

        Assert.Equal(Names(Total - 3 - 15, 15).Reverse().ToArray(), page.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Skip_without_take_returns_the_rest_in_order()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var rest = await session.Query<Person, PersonByName>().OrderBy(x => x.Name).Skip(30).ListAsync();

        Assert.Equal(Names(30, Total - 30), rest.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Loading_documents_by_id_keeps_the_requested_order()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var all = (await session.Query<Person, PersonByName>().OrderBy(x => x.Name).ListAsync()).ToList();
        var ids = all.Select(p => (long)p.Id).Reverse().ToArray();

        var loaded = await session.GetAsync<Person>(ids);

        Assert.Equal(all.Select(p => p.Name).Reverse().ToArray(), loaded.Select(p => p.Name).ToArray());
    }
}
