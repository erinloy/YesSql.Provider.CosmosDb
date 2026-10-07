using System.Net;
using Microsoft.Azure.Cosmos;
using YesSql.Indexes;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Inside a unit of work the provider starts a write without waiting for the response, so the writes of one save
/// overlap. These tests check that they do, that a failed write is thrown and not dropped, and that reads and
/// updates still see the writes before them.
/// </summary>
/// <remarks>
/// The provider shares one Cosmos client per account for the life of the process, and the options of the first
/// connection apply. These tests need their own request handler, so they reach the emulator by 127.0.0.1, which is a
/// different account key to the client cache than the localhost endpoint the other tests use.
/// </remarks>
public class PipelinedWriteTests
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

    private static readonly InstrumentedHandler Handler = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static async Task<IStore> NewStoreAsync(PartitionStrategy strategy) => (await NewStoreInDatabaseAsync(strategy)).Store;

    private static async Task<(IStore Store, CosmosDbOptions Options)> NewStoreInDatabaseAsync(PartitionStrategy strategy)
    {
        var endpoint = new Uri(Emulator.Endpoint);
        if (!endpoint.IsLoopback)
        {
            throw new InvalidOperationException("PipelinedWriteTests need a loopback emulator; COSMOS_TEST_ENDPOINT points elsewhere.");
        }

        var options = Emulator.Options(Emulator.NewDatabaseId("yessql_pipe"), strategy: strategy);
        var cosmosOptions = new CosmosDbOptions
        {
            AccountEndpoint = new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri.ToString(),
            AccountKey = options.AccountKey,
            DatabaseId = options.DatabaseId,
            PartitionStrategy = strategy,
            PartitionScope = options.PartitionScope,
            ClientOptions = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                HttpClientFactory = () => new HttpClient(Handler),
            },
        };
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(cosmosOptions));
        store.RegisterIndexes<PersonIndexProvider>();
        return (store, cosmosOptions);
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task The_writes_of_one_save_overlap(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        // The first save reserves its id blocks.
        await using (var warmup = store.CreateSession())
        {
            await warmup.SaveAsync(new Person { Name = "warmup" });
            await warmup.SaveChangesAsync();
        }

        Handler.Reset(delay: TimeSpan.FromMilliseconds(150));
        await using (var session = store.CreateSession())
        {
            await session.SaveAsync(new Person { Name = "first" });
            await session.SaveChangesAsync();
        }

        // The document and its index row are two writes. Written one after the other, each would wait for the
        // other and the peak would be one.
        Assert.True(Handler.Peak >= 2, $"peak of concurrent requests was {Handler.Peak}");
        Handler.Reset();
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Index_rows_keep_distinct_ids_when_the_counter_is_rebuilt_while_their_writes_are_in_flight(PartitionStrategy strategy)
    {
        // The conformance harness deletes every item, the counters too, between tests. A counter that is missing is
        // rebuilt from the largest stored id, and the rows written last are still in flight, so that id is behind the
        // ids this process has issued. Unguarded, the new block repeats ids and a row overwrites another.
        var (store, options) = await NewStoreInDatabaseAsync(strategy);

        await using (var session = store.CreateSession())
        {
            for (var i = 0; i < 3; i++)
            {
                await session.SaveAsync(new Person { Name = "first" + i });
            }

            await session.SaveChangesAsync();
        }

        using (var client = Emulator.NewClient())
        {
            var container = client.GetContainer(options.DatabaseId, options.ContainerId);
            try
            {
                await container.DeleteItemAsync<Newtonsoft.Json.Linq.JObject>("PersonByName", new PartitionKey("__seq"));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Assert.Fail("the counter of PersonByName should exist after three saves");
            }
        }

        // The first block holds ids 1 to 32, so 100 rows run it out and rebuild the counter. Writes are held back and a
        // query is not, so the query that finds the largest id cannot see the rows still in flight.
        Handler.Reset(delay: TimeSpan.FromMilliseconds(400), delayWritesOnly: true);
        try
        {
            await using var session = store.CreateSession();
            for (var i = 0; i < 100; i++)
            {
                await session.SaveAsync(new Person { Name = "later" + i });
            }

            await session.SaveChangesAsync();
        }
        finally
        {
            Handler.Reset();
        }

        await using var check = store.CreateSession();
        var rows = (await check.QueryIndex<PersonByName>().ListAsync()).ToList();
        Assert.Equal(103, rows.Count);
        Assert.Equal(103, rows.Select(r => r.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_failed_write_is_thrown_by_the_save_and_leaves_nothing(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        await using (var warmup = store.CreateSession())
        {
            await warmup.SaveAsync(new Person { Name = "warmup" });
            await warmup.SaveChangesAsync();
        }

        // Cosmos rejects the write of the index row. The document is written by then, or in flight.
        Handler.Reset(failWhenBodyContains: "\"__table\":\"PersonByName\"");
        try
        {
            await using var session = store.CreateSession();
            await session.SaveAsync(new Person { Name = "rejected" });
            var thrown = await Assert.ThrowsAsync<CosmosDbException>(() => session.SaveChangesAsync());
            Assert.Equal(HttpStatusCode.BadRequest, thrown.StatusCode);
        }
        finally
        {
            Handler.Reset();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(1, await session.Query<Person>().CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name == "rejected").CountAsync());
            Assert.Equal(1, await session.Query<Person, PersonByName>(x => x.Name == "warmup").CountAsync());
        }
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task A_failed_write_is_thrown_by_a_later_read_in_the_same_unit_of_work(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        await using (var warmup = store.CreateSession())
        {
            await warmup.SaveAsync(new Person { Name = "warmup" });
            await warmup.SaveChangesAsync();
        }

        Handler.Reset(failWhenBodyContains: "\"__table\":\"PersonByName\"");
        try
        {
            await using var session = store.CreateSession();
            await session.SaveAsync(new Person { Name = "rejected" });
            await Assert.ThrowsAsync<CosmosDbException>(() => session.Query<Person>().CountAsync());
        }
        finally
        {
            Handler.Reset();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(1, await session.Query<Person>().CountAsync());
        }
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task An_item_written_and_then_updated_in_one_unit_of_work_keeps_the_update(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        Person person;
        await using (var session = store.CreateSession())
        {
            person = new Person { Name = "before" };
            await session.SaveAsync(person);
            await session.FlushAsync();

            person.Name = "after";
            await session.SaveAsync(person);
            await session.FlushAsync();

            Assert.Equal("after", (await session.GetAsync<Person>(person.Id))!.Name);
            Assert.Equal(1, await session.Query<Person, PersonByName>(x => x.Name == "after").CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name == "before").CountAsync());
            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal("after", (await session.GetAsync<Person>(person.Id))!.Name);
            Assert.Equal(1, await session.Query<Person>().CountAsync());
            Assert.Equal(1, await session.Query<Person, PersonByName>(x => x.Name == "after").CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>(x => x.Name == "before").CountAsync());
        }
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Cancelling_after_pipelined_writes_undoes_all_of_them(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        await using (var session = store.CreateSession())
        {
            for (var i = 0; i < 20; i++)
            {
                await session.SaveAsync(new Person { Name = "p" + i });
            }

            await session.FlushAsync();
            await session.CancelAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(0, await session.Query<Person>().CountAsync());
            Assert.Equal(0, await session.Query<Person, PersonByName>().CountAsync());
        }
    }

    [Theory]
    [InlineData(PartitionStrategy.PerTable)]
    [InlineData(PartitionStrategy.PerStore)]
    public async Task Deletes_are_applied_by_the_save_and_a_cancelled_delete_is_restored(PartitionStrategy strategy)
    {
        var store = await NewStoreAsync(strategy);

        var ids = new List<int>();
        await using (var session = store.CreateSession())
        {
            for (var i = 0; i < 12; i++)
            {
                var person = new Person { Name = "p" + i };
                await session.SaveAsync(person);
                ids.Add(person.Id);
            }

            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            foreach (var person in await session.GetAsync<Person>(ids.Take(6).ToArray()))
            {
                session.Delete(person);
            }

            await session.FlushAsync();
            await session.CancelAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(12, await session.Query<Person>().CountAsync());
            Assert.Equal(12, await session.Query<Person, PersonByName>().CountAsync());
        }

        await using (var session = store.CreateSession())
        {
            foreach (var person in await session.GetAsync<Person>(ids.Take(6).ToArray()))
            {
                session.Delete(person);
            }

            await session.SaveChangesAsync();
        }

        await using (var session = store.CreateSession())
        {
            Assert.Equal(6, await session.Query<Person>().CountAsync());
            Assert.Equal(6, await session.Query<Person, PersonByName>().CountAsync());
            Assert.Empty(await session.GetAsync<Person>(ids.Take(6).ToArray()));
        }
    }

    // Counts the requests in flight, can hold each one back, and can answer a matching write with a 400.
    private sealed class InstrumentedHandler : DelegatingHandler
    {
        private int _inFlight;
        private int _peak;
        private TimeSpan _delay;
        private bool _delayWritesOnly;
        private string? _failWhenBodyContains;

        public InstrumentedHandler(HttpMessageHandler inner) : base(inner)
        {
        }

        public int Peak => Volatile.Read(ref _peak);

        public void Reset(TimeSpan delay = default, string? failWhenBodyContains = null, bool delayWritesOnly = false)
        {
            _delay = delay;
            _delayWritesOnly = delayWritesOnly;
            _failWhenBodyContains = failWhenBodyContains;
            Volatile.Write(ref _peak, 0);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_failWhenBodyContains is { } marker && request.Method == HttpMethod.Post && request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync();
                if ((await request.Content.ReadAsStringAsync(cancellationToken)).Contains(marker, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"code\":\"BadRequest\",\"message\":\"rejected by the test\"}"),
                        RequestMessage = request,
                    };
                }
            }

            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }

            try
            {
                // A query carries this header; every other POST, and any PUT or DELETE, writes.
                var isWrite = request.Method != HttpMethod.Get && !request.Headers.Contains("x-ms-documentdb-isquery");
                if (_delay > TimeSpan.Zero && (!_delayWritesOnly || isWrite))
                {
                    await Task.Delay(_delay, cancellationToken);
                }

                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
