using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// Runs an asynchronous initialization once per key and shares the result between concurrent callers. A
/// failed initialization is not remembered: the next caller runs it again, so a transient failure does not
/// poison every later connection.
/// </summary>
internal sealed class AsyncOnceCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _entries = new();

    public async Task EnsureAsync(string key, Func<Task> initialize)
    {
        var entry = _entries.GetOrAdd(key, _ => new Lazy<Task>(initialize));
        try
        {
            await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            // Remove only this attempt, not a newer one that another caller may already have started.
            _entries.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, entry));
            throw;
        }
    }
}
