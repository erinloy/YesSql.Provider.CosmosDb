using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Ordering and paging of documents queried through a reduce index.
/// </summary>
public class ReduceIndexOrderTests
{
    public class Article
    {
        public int Id { get; set; }
        public int Day { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    public class ArticlesByDay : ReduceIndex
    {
        public int Day { get; set; }
        public int Count { get; set; }
    }

    public class ArticleIndexProvider : IndexProvider<Article>
    {
        public override void Describe(DescribeContext<Article> context)
            => context.For<ArticlesByDay, int>()
                .Map(article => new ArticlesByDay { Day = article.Day, Count = 1 })
                .Group(index => index.Day)
                .Reduce(group => new ArticlesByDay { Day = group.Key, Count = group.Sum(x => x.Count) })
                .Delete((index, map) =>
                {
                    index.Count -= map.Sum(x => x.Count);
                    return index.Count > 0 ? index : null!;
                });
    }

    private static async Task<IStore> NewStoreWithArticlesAsync()
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_reduce"))));
        store.RegisterIndexes<ArticleIndexProvider>();

        await using var session = store.CreateSession();

        // Day 1 has three articles, day 2 has one, day 3 has two, day 4 has five.
        var perDay = new Dictionary<int, int> { [1] = 3, [2] = 1, [3] = 2, [4] = 5 };
        foreach (var (day, count) in perDay)
        {
            for (var i = 0; i < count; i++)
            {
                await session.SaveAsync(new Article { Day = day, Title = $"d{day}-{i}" });
            }
        }

        await session.SaveChangesAsync();
        return store;
    }

    [Fact]
    public async Task Documents_are_ordered_by_a_reduce_index_column()
    {
        var store = await NewStoreWithArticlesAsync();
        await using var session = store.CreateSession();

        var ascending = (await session.Query<Article, ArticlesByDay>().OrderBy(x => x.Count).ListAsync()).Select(a => a.Day).ToArray();
        var descending = (await session.Query<Article, ArticlesByDay>().OrderByDescending(x => x.Count).ListAsync()).Select(a => a.Day).ToArray();

        // The documents of the day with the fewest articles come first (day 2, then 3, then 1, then 4).
        Assert.Equal([2, 3, 3, 1, 1, 1, 4, 4, 4, 4, 4], ascending);
        Assert.Equal([4, 4, 4, 4, 4, 1, 1, 1, 3, 3, 2], descending);
    }

    [Fact]
    public async Task A_page_of_a_reduce_index_query_follows_the_order()
    {
        var store = await NewStoreWithArticlesAsync();
        await using var session = store.CreateSession();

        var page = (await session.Query<Article, ArticlesByDay>().OrderBy(x => x.Count).Skip(1).Take(4).ListAsync()).Select(a => a.Day).ToArray();

        Assert.Equal([3, 3, 1, 1], page);
    }
}
