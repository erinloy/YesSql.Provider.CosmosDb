using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// YesSql orders a paged query by the id of the document when the caller gave no order, so that pages are stable.
/// For a query through a reduce index that is an <c>ORDER BY</c> on the document table, which the index rows do not carry.
/// </summary>
public class ReduceQueryOrderTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
    }

    public class CityCount : ReduceIndex
    {
        public string City { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
            => context.For<CityCount, string>()
                .Map(p => new CityCount { City = p.City, Count = 1 })
                .Group(i => i.City)
                .Reduce(g => new CityCount { City = g.Key, Count = g.Sum(x => x.Count) })
                .Delete((index, map) => { index.Count -= map.Sum(x => x.Count); return index.Count > 0 ? index : null; });
    }

    private static async Task<(IStore Store, List<int> Austin)> SeedAsync(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_rorder"), strategy: strategy)));
        store.RegisterIndexes<PersonIndexProvider>();

        var austin = new List<int>();
        await using var session = store.CreateSession();
        for (var i = 0; i < 12; i++)
        {
            var person = new Person { Name = "p" + i, City = i % 3 == 0 ? "Boston" : "Austin" };
            await session.SaveAsync(person);
            await session.FlushAsync();
            if (person.City == "Austin")
            {
                austin.Add(person.Id);
            }
        }

        await session.SaveChangesAsync();
        return (store, austin);
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Pages_of_documents_through_a_reduce_index_are_ordered_by_document_id(PartitionStrategy strategy)
    {
        var (store, austin) = await SeedAsync(strategy);
        Assert.Equal(8, austin.Count);

        await using var session = store.CreateSession();
        var first = (await session.Query<Person, CityCount>(x => x.City == "Austin").Take(3).ListAsync()).Select(p => p.Id).ToList();
        var second = (await session.Query<Person, CityCount>(x => x.City == "Austin").Skip(3).Take(3).ListAsync()).Select(p => p.Id).ToList();
        var rest = (await session.Query<Person, CityCount>(x => x.City == "Austin").Skip(6).ListAsync()).Select(p => p.Id).ToList();
        var everything = (await session.Query<Person, CityCount>(x => x.City == "Austin").ListAsync()).Select(p => p.Id).OrderBy(id => id).ToList();

        var sorted = austin.OrderBy(id => id).ToList();
        Assert.Equal(sorted.Take(3), first);
        Assert.Equal(sorted.Skip(3).Take(3), second);
        Assert.Equal(sorted.Skip(6), rest);
        Assert.Equal(sorted, everything);
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task An_order_on_an_index_column_comes_first_and_the_document_id_breaks_ties(PartitionStrategy strategy)
    {
        var (store, austin) = await SeedAsync(strategy);

        await using var session = store.CreateSession();
        var bigFirst = (await session.Query<Person, CityCount>().OrderByDescending(x => x.Count).Take(100).ListAsync()).ToList();
        var smallFirst = (await session.Query<Person, CityCount>().OrderBy(x => x.Count).Take(100).ListAsync()).ToList();

        // Austin has 8 people and Boston 4.
        Assert.Equal(austin.OrderBy(id => id), bigFirst.Take(8).Select(p => p.Id));
        Assert.All(bigFirst.Skip(8), p => Assert.Equal("Boston", p.City));
        Assert.All(smallFirst.Take(4), p => Assert.Equal("Boston", p.City));
        Assert.Equal(austin.OrderBy(id => id), smallFirst.Skip(4).Select(p => p.Id));
    }

    [Fact]
    public async Task A_page_through_a_reduce_index_over_all_its_rows_keeps_each_document_once()
    {
        var (store, _) = await SeedAsync(PartitionStrategy.PerStore);

        await using var session = store.CreateSession();
        var all = (await session.Query<Person, CityCount>().Take(100).ListAsync()).Select(p => p.Id).ToList();

        Assert.Equal(12, all.Count);
        Assert.Equal(all.OrderBy(id => id), all);
        Assert.Equal(all.Distinct().Count(), all.Count);
    }
}
