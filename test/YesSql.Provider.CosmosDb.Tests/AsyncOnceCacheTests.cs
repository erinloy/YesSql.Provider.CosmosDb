using YesSql.Provider.CosmosDb.Internal;

namespace YesSql.Provider.CosmosDb.Tests;

public class AsyncOnceCacheTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_initialization()
    {
        var cache = new AsyncOnceCache();
        var runs = 0;
        var release = new TaskCompletionSource();

        async Task Initialize()
        {
            Interlocked.Increment(ref runs);
            await release.Task;
        }

        var callers = Enumerable.Range(0, 10).Select(_ => cache.EnsureAsync("key", Initialize)).ToArray();
        release.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_successful_initialization_is_not_repeated()
    {
        var cache = new AsyncOnceCache();
        var runs = 0;

        await cache.EnsureAsync("key", () => { runs++; return Task.CompletedTask; });
        await cache.EnsureAsync("key", () => { runs++; return Task.CompletedTask; });

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_failed_initialization_is_retried_by_the_next_caller()
    {
        var cache = new AsyncOnceCache();
        var runs = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.EnsureAsync("key", () =>
        {
            runs++;
            throw new InvalidOperationException("transient");
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.EnsureAsync("key", async () =>
        {
            runs++;
            await Task.Yield();
            throw new InvalidOperationException("transient");
        }));

        await cache.EnsureAsync("key", () => { runs++; return Task.CompletedTask; });

        Assert.Equal(3, runs);

        // Now that it succeeded, it is cached.
        await cache.EnsureAsync("key", () => { runs++; return Task.CompletedTask; });
        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var cache = new AsyncOnceCache();
        var runs = 0;

        await cache.EnsureAsync("a", () => { runs++; return Task.CompletedTask; });
        await cache.EnsureAsync("b", () => { runs++; return Task.CompletedTask; });

        Assert.Equal(2, runs);
    }
}
