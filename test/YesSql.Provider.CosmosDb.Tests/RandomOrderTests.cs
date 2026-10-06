using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// OrderByRandom. Cosmos cannot order by a function, so the provider orders and pages these queries in the client.
/// </summary>
public class RandomOrderTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
    }

    public class PersonByName : MapIndex
    {
        public string Name { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
            => context.For<PersonByName>().Map(p => new PersonByName { Name = p.Name, Group = p.Group });
    }

    private const int Total = 60;

    private static async Task<IStore> NewStoreWithPeopleAsync()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_random"))));
        store.RegisterIndexes<PersonIndexProvider>();

        await using var session = store.CreateSession();
        for (var i = 0; i < Total; i++)
        {
            await session.SaveAsync(new Person { Name = $"n{i:00}", Group = i < Total / 2 ? "A" : "B" });
        }

        await session.SaveChangesAsync();
        return store;
    }

    private static string[] Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Index_queries_return_every_row_in_a_shuffled_order()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var first = (await session.Query<Person, PersonByName>().OrderByRandom().ListAsync()).Select(p => p.Name).ToArray();
        var second = (await session.Query<Person, PersonByName>().OrderByRandom().ListAsync()).Select(p => p.Name).ToArray();

        Assert.Equal(Total, first.Length);
        Assert.Equal(Sorted(first), Sorted(second));
        Assert.NotEqual(Sorted(first), first);
        Assert.NotEqual(first, second); // two shuffles of 60 rows are equal only by a 1-in-60! chance
    }

    [Fact]
    public async Task A_random_page_is_a_sample_of_the_whole_set_not_the_first_rows()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var samples = new List<string[]>();
        for (var i = 0; i < 5; i++)
        {
            var page = (await session.Query<Person, PersonByName>().OrderByRandom().Take(10).ListAsync()).Select(p => p.Name).ToArray();
            Assert.Equal(10, page.Distinct().Count());
            samples.Add(page);
        }

        // Five samples of ten out of sixty are not all the same ten rows.
        Assert.True(samples.Select(Sorted).Select(s => string.Join(",", s)).Distinct().Count() > 1);
    }

    [Fact]
    public async Task Random_after_an_ordering_keeps_the_primary_order()
    {
        var store = await NewStoreWithPeopleAsync();
        await using var session = store.CreateSession();

        var rows = (await session.Query<Person, PersonByName>().OrderBy(x => x.Group).ThenByRandom().ListAsync()).ToList();

        Assert.Equal(Total, rows.Count);
        Assert.All(rows.Take(Total / 2), p => Assert.Equal("A", p.Group));
        Assert.All(rows.Skip(Total / 2), p => Assert.Equal("B", p.Group));
        Assert.NotEqual(rows.Take(Total / 2).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal), rows.Take(Total / 2).Select(p => p.Name));
    }
}
