using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbTransaction"/> shim for a YesSql unit of work. Writes are applied eagerly (so
/// YesSql's autoflush read-your-writes works), and each one records its inverse in an undo log. On
/// rollback the unit of work is reverted: in <see cref="PartitionStrategy.PerStore"/> every item shares
/// one logical partition, so the inverse ops are sent as Cosmos transactional batches (atomic per batch of up
/// to 100 operations; a rejected batch is retried item by item); in <see cref="PartitionStrategy.PerTable"/>
/// they span partitions, so rollback is best-effort per item.
/// <para>
/// Writes are started without waiting for the response (see <see cref="CreateAsync"/>), so the round trips of one
/// save overlap. Every other command, commit and rollback first waits for the writes in flight
/// (<see cref="CompleteWritesAsync"/>), and a failed write is thrown there, never dropped.
/// </para>
/// </summary>
internal sealed class CosmosDbTransaction : DbTransaction
{
    private readonly CosmosDbConnection _connection;
    // Writes that are started and not yet answered. A single unit of work holds this many Cosmos requests at once.
    private const int MaxWritesInFlight = 8;

    private readonly List<UndoOp> _undo = new();
    private readonly SemaphoreSlim _slots = new(MaxWritesInFlight);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();
    private Exception? _failure;
    private bool _committed;

    public CosmosDbTransaction(CosmosDbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    protected override DbConnection DbConnection => _connection;
    public override IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Record the inverse of a write. <paramref name="prior"/> == null means the write created the item
    /// (undo = delete); otherwise undo restores the prior snapshot (undo = upsert). Writes complete on other threads,
    /// so the log is guarded.
    /// </summary>
    internal void Record(string id, string partitionKey, JObject? prior)
    {
        lock (_undo)
        {
            _undo.Add(new UndoOp(id, partitionKey, prior));
        }
    }

    /// <summary>
    /// Creates an item. It fails if an item with the same id already exists, as an INSERT of a duplicate key does. The
    /// request is started and this returns once it is under way, so the next write of the same save can start before
    /// this one is answered. A failure is kept and thrown by the next command that waits for the writes in flight, or by
    /// commit. The undo (delete) is recorded when the item has been created, never for an item that was already there.
    /// </summary>
    internal Task CreateAsync(Container container, JObject item, PartitionKey partitionKey, string partitionKeyValue, CancellationToken cancellationToken)
    {
        var itemId = item["id"]!.ToString();
        return StartAsync(itemId, async () =>
        {
            await container.CreateItemAsync(item, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            Record(itemId, partitionKeyValue, null);
        }, cancellationToken);
    }

    /// <summary>
    /// Replaces an item that exists, started and not awaited like <see cref="CreateAsync"/>. The caller records the
    /// undo (the prior state) before it calls this.
    /// </summary>
    internal Task ReplaceAsync(Container container, JObject item, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        var itemId = item["id"]!.ToString();
        return StartAsync(itemId, () => container.ReplaceItemAsync(item, itemId, partitionKey, cancellationToken: cancellationToken), cancellationToken);
    }

    /// <summary>Deletes an item, started and not awaited like <see cref="CreateAsync"/>. An item that is already gone is not a failure.</summary>
    internal Task DeleteAsync(Container container, string itemId, PartitionKey partitionKey, CancellationToken cancellationToken)
        => StartAsync(itemId, async () =>
        {
            try
            {
                await container.DeleteItemAsync<JObject>(itemId, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // already gone
            }
        }, cancellationToken);

    private async Task StartAsync(string itemId, Func<Task> write, CancellationToken cancellationToken)
    {
        ThrowIfFailed();

        // Two writes of one item must reach Cosmos in the order they were issued.
        if (_inFlight.TryGetValue(itemId, out var earlier))
        {
            await earlier.ConfigureAwait(false);
            ThrowIfFailed();
        }

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight[itemId] = finished.Task;
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await write().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ex is CosmosException cosmos ? new CosmosDbException(cosmos) : ex, null);
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Task>(itemId, finished.Task));
                _slots.Release();
                finished.SetResult();
            }
        }
    }

    /// <summary>Waits for the write in flight to one item, if there is one, and throws the first write that failed.</summary>
    internal async Task CompleteWriteAsync(string itemId)
    {
        if (_inFlight.TryGetValue(itemId, out var write))
        {
            await write.ConfigureAwait(false);
        }

        ThrowIfFailed();
    }

    /// <summary>Waits for the writes in flight, and throws the first one that failed.</summary>
    internal async Task CompleteWritesAsync()
    {
        await WaitForWritesAsync().ConfigureAwait(false);
        ThrowIfFailed();
    }

    private async Task WaitForWritesAsync()
    {
        while (!_inFlight.IsEmpty)
        {
            await Task.WhenAll(_inFlight.Values).ConfigureAwait(false);
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task CommitAsync(CancellationToken cancellationToken)
    {
        // Writes are applied as they are issued; commit waits for the last of them, and then discards the undo log.
        // A write that failed means the unit of work is not committed.
        await CompleteWritesAsync().ConfigureAwait(false);
        _committed = true;
        lock (_undo)
        {
            _undo.Clear();
        }

        _connection.EndTransaction(this);
    }

    // ADO.NET rolls an uncommitted transaction back when it is disposed. YesSql relies on that: ISession.CancelAsync
    // and its handling of a failed read or query release the transaction by disposing it, without calling Rollback.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_committed)
        {
            Rollback();
        }

        if (disposing)
        {
            _connection.EndTransaction(this);
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _connection.EndTransaction(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The writes in flight have to land before they can be undone. A failed one is not rethrown here: the
            // unit of work is being undone, and its failure is thrown to the caller by the command or commit that saw it.
            await WaitForWritesAsync().ConfigureAwait(false);
            await RollbackCoreAsync(cancellationToken);
        }
        catch (CosmosException ex)
        {
            throw new CosmosDbException(ex);
        }
    }

    // Restores the items the unit of work touched. An operation leaves the log once it has been applied, so a rollback
    // that fails partway can be run again for the rest.
    private async Task RollbackCoreAsync(CancellationToken cancellationToken)
    {
        if (_committed)
        {
            return;
        }

        // Only the first record for an item matters: it holds the item's state from before the unit of work
        // touched it, which is what rollback restores. Keeping one operation per item keeps the number of
        // operations, and so the number of batches, down to the number of items touched.
        List<UndoOp> ops;
        lock (_undo)
        {
            ops = _undo.GroupBy(op => (op.PartitionKey, op.Id)).Select(group => group.First()).ToList();
        }

        var container = _connection.CosmosContainer;

        if (_connection.Options.PartitionStrategy == PartitionStrategy.PerStore)
        {
            var partitionKey = new PartitionKey(_connection.Options.PartitionScope);
            foreach (var chunk in Chunk(ops, 100))
            {
                var batch = container.CreateTransactionalBatch(partitionKey);
                foreach (var op in chunk)
                {
                    if (op.Prior is null)
                    {
                        batch.DeleteItem(op.Id);
                    }
                    else
                    {
                        batch.UpsertItem(op.Prior);
                    }
                }

                using var response = await batch.ExecuteAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    // A batch is all or nothing, and it is refused when one operation cannot apply, for example the
                    // delete of an item that is already gone. Apply its operations one at a time instead: that is
                    // no longer atomic, and anything other than a missing item is thrown.
                    foreach (var op in chunk)
                    {
                        await ApplyOneAsync(container, partitionKey, op, cancellationToken);
                    }
                }

                Applied(chunk);
            }
        }
        else
        {
            foreach (var op in ops)
            {
                await ApplyOneAsync(container, new PartitionKey(op.PartitionKey), op, cancellationToken);
                Applied([op]);
            }
        }
    }

    // Takes the operations that have been applied, and every earlier record of the same items, out of the log.
    private void Applied(IEnumerable<UndoOp> applied)
    {
        var items = applied.Select(op => (op.PartitionKey, op.Id)).ToHashSet();
        lock (_undo)
        {
            _undo.RemoveAll(op => items.Contains((op.PartitionKey, op.Id)));
        }
    }

    private static async Task ApplyOneAsync(Container container, PartitionKey partitionKey, UndoOp op, CancellationToken cancellationToken)
    {
        try
        {
            if (op.Prior is null)
            {
                await container.DeleteItemAsync<JObject>(op.Id, partitionKey, cancellationToken: cancellationToken);
            }
            else
            {
                await container.UpsertItemAsync(op.Prior, partitionKey, cancellationToken: cancellationToken);
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // already in the desired state
        }
    }

    private static IEnumerable<List<UndoOp>> Chunk(List<UndoOp> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
        }
    }

    private readonly record struct UndoOp(string Id, string PartitionKey, JObject? Prior);
}
