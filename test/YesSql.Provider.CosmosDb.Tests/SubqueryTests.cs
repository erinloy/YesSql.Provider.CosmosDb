using YesSql.Indexes;
using YesSql.Services;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// "x IN (SELECT ...)" predicates. The provider runs the inner query first and passes its values to the outer
/// query, so the values must reach it exactly as stored, whatever characters they contain.
/// </summary>
public class SubqueryTests
{
    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
    }

    public class PersonByName : MapIndex
    {
        public string Name { get; set; } = string.Empty;
    }

    public class PersonByNickname : MapIndex
    {
        public string Nickname { get; set; } = string.Empty;
    }

    public class PersonIndexProvider : IndexProvider<Person>
    {
        public override void Describe(DescribeContext<Person> context)
        {
            context.For<PersonByName>().Map(p => new PersonByName { Name = p.Name });
            context.For<PersonByNickname>().Map(p => new PersonByNickname { Nickname = p.Nickname });
        }
    }

    private static async Task<IStore> NewStoreAsync(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_subq"), strategy: strategy)));
        store.RegisterIndexes<PersonIndexProvider>();
        return store;
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Values_with_quotes_and_backslashes_reach_the_outer_query_unchanged(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        // Values that break a query when they are written into its text: a trailing backslash escapes the closing
        // quote, a quote ends the string, and so on.
        string[] tricky = [@"ends-with-backslash\", "it's", @"say ""hi""", @"a\'b", "plain", "line\nbreak", "ünïcode"];

        await using (var session = store.CreateSession())
        {
            foreach (var value in tricky)
            {
                await session.SaveAsync(new Person { Name = value, Nickname = value });
            }

            await session.SaveAsync(new Person { Name = "not-a-nickname", Nickname = "unrelated" });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            // People whose nickname equals the name of some person other than "not-a-nickname".
            var matches = await session.Query<Person, PersonByNickname>()
                .Where(x => x.Nickname.IsIn<PersonByName>(y => y.Name, y => y.Name != "not-a-nickname"))
                .ListAsync();

            Assert.Equal(tricky.OrderBy(v => v, StringComparer.Ordinal), matches.Select(p => p.Nickname).OrderBy(v => v, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task An_IN_subquery_with_no_matching_values_returns_nothing()
    {
        var store = await NewStoreAsync(PartitionStrategy.PerTable);

        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "a", Nickname = "x" });
            await session.SaveAsync(new Person { Name = "b", Nickname = "y" });
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            var query = session.Query<Person, PersonByNickname>()
                .Where(x => x.Nickname.IsIn<PersonByName>(y => y.Name, y => y.Name == "nobody"));
            Assert.Equal(0, await query.CountAsync());
        }
    }
}
