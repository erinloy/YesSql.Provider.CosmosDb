using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// ADO.NET <see cref="DbTransaction"/> shim for a YesSql unit of work. Writes are applied eagerly (so
/// YesSql's autoflush read-your-writes works), and each one records its inverse in an undo log. On
/// rollback the unit of work is reverted: in <see cref="PartitionStrategy.PerStore"/> every item shares
/// one logical partition, so the inverse ops are applied atomically via a Cosmos transactional batch; in
/// <see cref="PartitionStrategy.PerTable"/> they span partitions, so rollback is best-effort per item.
/// </summary>
public sealed class CosmosDbTransaction : DbTransaction
{
    private readonly CosmosDbConnection _connection;
    private readonly List<UndoOp> _undo = new();
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
    /// (undo = delete); otherwise undo restores the prior snapshot (undo = upsert).
    /// </summary>
    internal void Record(string id, string partitionKey, JObject? prior)
        => _undo.Add(new UndoOp(id, partitionKey, prior));

    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task CommitAsync(CancellationToken cancellationToken)
    {
        // Writes were already applied; commit just discards the undo log.
        _committed = true;
        _undo.Clear();
        return Task.CompletedTask;
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_committed || _undo.Count == 0)
        {
            return;
        }

        var container = _connection.CosmosContainer;
        var ops = Enumerable.Reverse(_undo).ToList(); // undo in reverse order
        _undo.Clear();

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
                    // Atomic restore failed (e.g. deleting an already-gone item aborts the batch) — fall
                    // back to best-effort per item so the rollback still completes.
                    foreach (var op in chunk)
                    {
                        await ApplyOneAsync(container, partitionKey, op, cancellationToken);
                    }
                }
            }
        }
        else
        {
            foreach (var op in ops)
            {
                await ApplyOneAsync(container, new PartitionKey(op.PartitionKey), op, cancellationToken);
            }
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
