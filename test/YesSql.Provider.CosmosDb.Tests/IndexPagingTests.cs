using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// A count or a page of a query over one index is answered by Cosmos from the distinct document ids, in document id
/// order, without reading every matching index row. These tests use an index that has several rows per document, so a
/// result that failed to remove duplicates or to order by document id would show.
/// </summary>
public class IndexPagingTests
{
    public class Article
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string[] Tags { get; set; } = Array.Empty<string>();
    }

    public class ArticleByTag : MapIndex
    {
        public string Tag { get; set; } = string.Empty;
    }

    public class ArticleIndexProvider : IndexProvider<Article>
    {
        public override void Describe(DescribeContext<Article> context)
            => context.For<ArticleByTag>().Map(a => a.Tags.Select(tag => new ArticleByTag { Tag = tag }));
    }

    // 30 articles; article i has tag "all", "even" or "odd", and every third one also "third".
    private static async Task<(IStore Store, List<int> Ids)> SeedAsync(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_paging"), strategy: strategy)));
        store.RegisterIndexes<ArticleIndexProvider>();

        var ids = new List<int>();
        await using var session = store.CreateSession();
        for (var i = 0; i < 30; i++)
        {
            var tags = new List<string> { "all", i % 2 == 0 ? "even" : "odd" };
            if (i % 3 == 0)
            {
                tags.Add("third");
            }

            var article = new Article { Title = "a" + i, Tags = tags.ToArray() };
            await session.SaveAsync(article);
            await session.FlushAsync();
            ids.Add(article.Id);
        }

        await session.SaveChangesAsync();
        return (store, ids);
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_count_removes_documents_matched_by_several_index_rows(PartitionStrategy strategy)
    {
        var (store, ids) = await SeedAsync(strategy);

        await using var session = store.CreateSession();

        // every article has an "all" row, and 10 of them also have a "third" row
        Assert.Equal(30, await session.Query<Article, ArticleByTag>(x => x.Tag == "all").CountAsync());
        Assert.Equal(10, await session.Query<Article, ArticleByTag>(x => x.Tag == "third").CountAsync());
        Assert.Equal(30, await session.Query<Article, ArticleByTag>(x => x.Tag == "all" || x.Tag == "even" || x.Tag == "third").CountAsync());
        Assert.Equal(0, await session.Query<Article, ArticleByTag>(x => x.Tag == "missing").CountAsync());
        Assert.Equal(30, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Pages_are_distinct_and_in_document_id_order(PartitionStrategy strategy)
    {
        var (store, ids) = await SeedAsync(strategy);
        var sorted = ids.OrderBy(id => id).ToList();

        await using var session = store.CreateSession();

        // each article matches several of these tags, so there are about 70 index rows for 30 documents
        async Task<List<int>> Page(int skip, int? take)
        {
            var query = session.Query<Article, ArticleByTag>(x => x.Tag == "all" || x.Tag == "even" || x.Tag == "third").Skip(skip);
            var documents = take is { } n ? await query.Take(n).ListAsync() : await query.ListAsync();
            return documents.Select(a => a.Id).ToList();
        }

        Assert.Equal(sorted.Take(7), await Page(0, 7));
        Assert.Equal(sorted.Skip(7).Take(7), await Page(7, 7));
        Assert.Equal(sorted.Skip(14).Take(7), await Page(14, 7));
        Assert.Equal(sorted.Skip(28), await Page(28, 7));
        Assert.Equal(sorted.Skip(25), await Page(25, null));
        Assert.Empty(await Page(30, 7));
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task The_first_match_is_found(PartitionStrategy strategy)
    {
        var (store, ids) = await SeedAsync(strategy);

        await using var session = store.CreateSession();

        var first = await session.Query<Article, ArticleByTag>(x => x.Tag == "third").FirstOrDefaultAsync();
        Assert.NotNull(first);
        Assert.Contains(first!.Id, ids);
        Assert.Null(await session.Query<Article, ArticleByTag>(x => x.Tag == "missing").FirstOrDefaultAsync());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_page_larger_than_one_document_query_comes_back_whole_and_in_order(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_paging"), strategy: strategy)));
        store.RegisterIndexes<ArticleIndexProvider>();

        const int count = 230; // more than two queries' worth of documents
        var ids = new List<int>();
        await using (var session = store.CreateSession())
        {
            for (var i = 0; i < count; i++)
            {
                var article = new Article { Title = "a" + i, Tags = new[] { "bulk", "also" } };
                await session.SaveAsync(article);
                ids.Add(article.Id);
            }

            await session.SaveChangesAsync();
        }

        await using var read = store.CreateSession();
        var page = (await read.Query<Article, ArticleByTag>(x => x.Tag == "bulk" || x.Tag == "also").Take(count + 20).ListAsync()).Select(a => a.Id).ToList();
        Assert.Equal(ids.OrderBy(id => id), page);

        // documents loaded by id come back in the order they were asked for, and an id with no document is skipped
        var asked = ids.AsEnumerable().Reverse().Take(150).Append(int.MaxValue).ToArray();
        var loaded = (await read.GetAsync<Article>(asked)).Select(a => a.Id).ToList();
        Assert.Equal(asked.Take(150), loaded);
    }

    [Fact]
    public async Task A_query_that_filters_on_the_documents_type_is_still_counted_and_paged_correctly()
    {
        var (store, _) = await SeedAsync(PartitionStrategy.PerStore);

        await using var session = store.CreateSession();

        // a type filter cannot be paged in Cosmos, so these take the general path
        Assert.Equal(30, await session.Query<Article, ArticleByTag>(x => x.Tag == "all", filterType: true).CountAsync());
        Assert.Equal(5, (await session.Query<Article, ArticleByTag>(x => x.Tag == "all", filterType: true).Take(5).ListAsync()).Count());
    }
}
