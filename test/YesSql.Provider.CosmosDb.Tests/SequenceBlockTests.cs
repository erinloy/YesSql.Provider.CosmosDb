using YesSql.Indexes;
using YesSql.Provider.CosmosDb.Internal;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>Index row ids are reserved in blocks, so inserts do not contend on one counter document.</summary>
public class SequenceBlockTests
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

    [Fact]
    public async Task A_block_is_reserved_once_per_block_size_ids_and_ids_are_not_repeated()
    {
        var reservations = 0;
        long counter = 0;
        var key = "unit-" + Guid.NewGuid();

        Task<long> Reserve(int size, CancellationToken token)
        {
            reservations++;
            var first = counter + 1;
            counter += size;
            return Task.FromResult(first);
        }

        var ids = new List<long>();
        for (var i = 0; i < SequenceBlocks.BlockSize * 3 + 1; i++)
        {
            ids.Add(await SequenceBlocks.NextAsync(key, Reserve, CancellationToken.None));
        }

        Assert.Equal(4, reservations);
        Assert.Equal(Enumerable.Range(1, ids.Count).Select(i => (long)i), ids);
    }

    [Fact]
    public async Task Concurrent_callers_get_distinct_ids()
    {
        long counter = 0;
        var key = "unit-" + Guid.NewGuid();

        async Task<long> Reserve(int size, CancellationToken token)
        {
            await Task.Delay(5, token);
            return Interlocked.Add(ref counter, size) - size + 1;
        }

        var ids = await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => Task.Run(() => SequenceBlocks.NextAsync(key, Reserve, CancellationToken.None))));

        Assert.Equal(500, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Many_sessions_saving_at_once_all_succeed_with_distinct_index_ids(PartitionStrategy strategy)
    {
        var store = await StoreFactory.CreateAndInitializeAsync(
            new Configuration().UseCosmosDb(Emulator.Options(Emulator.NewDatabaseId("yessql_seq"), strategy: strategy)));
        store.RegisterIndexes<PersonIndexProvider>();

        const int count = 120;
        var gate = new SemaphoreSlim(24);
        await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                await using var session = store.CreateSession();
                await session.SaveAsync(new Person { Name = "p" + i });
                await session.SaveChangesAsync();
            }
            finally
            {
                gate.Release();
            }
        }));

        await using var check = store.CreateSession();
        var rows = (await check.QueryIndex<PersonByName>().ListAsync()).ToList();
        Assert.Equal(count, rows.Count);
        Assert.Equal(count, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal(count, await check.Query<Person>().CountAsync());
    }

    [Fact]
    public async Task A_second_process_reserves_the_next_block_and_never_the_same_ids()
    {
        var options = Emulator.Options(Emulator.NewDatabaseId("yessql_seq"), strategy: PartitionStrategy.PerStore);

        async Task<List<long>> InsertThreeAsync()
        {
            var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(options));
            store.RegisterIndexes<PersonIndexProvider>();
            await using (var session = store.CreateSession())
            {
                for (var i = 0; i < 3; i++)
                {
                    await session.SaveAsync(new Person { Name = "p" + i });
                }

                await session.SaveChangesAsync();
            }

            await using var read = store.CreateSession();
            return (await read.QueryIndex<PersonByName>().ListAsync()).Select(r => r.Id).OrderBy(id => id).ToList();
        }

        var first = await InsertThreeAsync();

        // A new process has no cached block, so it reserves the next one from the counter.
        SequenceBlocks.ClearForTests();
        var both = await InsertThreeAsync();

        Assert.Equal(6, both.Count);
        Assert.Equal(6, both.Distinct().Count());
        Assert.Equal(first, both.Take(3));
        Assert.True(both[3] > first[^1] + 1, "the second block starts after the whole first block");
        Assert.True(both[3] > SequenceBlocks.BlockSize, "the second block is the next one in the counter");
    }
}
