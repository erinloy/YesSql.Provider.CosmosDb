using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// Hands out ids from blocks that were reserved in the store. Reserving an id takes a read and a conditional write on
/// a counter document, and concurrent writers that share a counter collide on its ETag and retry. Reserving a block
/// of ids at once makes that cost one in <c>blockSize</c> inserts, and the other ids are handed out in memory.
/// </summary>
/// <remarks>
/// Ids stay unique across processes, because each block is reserved with a conditional write. They have gaps: the
/// unused part of a block is lost when the process stops, as the ids of a rolled back identity insert are in a
/// relational database.
///
/// A process never hands out an id twice, even when the stored counter is behind the ids it has issued. The counter
/// can be behind if it was deleted, or if it is rebuilt from the largest stored id while writes that carry issued ids
/// are still in flight. Every reservation is asked to start after the last id this process issued.
/// </remarks>
internal static class SequenceBlocks
{
    /// <summary>The number of ids reserved at once.</summary>
    public const int BlockSize = 32;

    private static readonly ConcurrentDictionary<string, Block> Blocks = new(StringComparer.Ordinal);

    private sealed class Block
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Next;
        public long Last;
    }

    /// <summary>Forgets every cached block, as a new process would have none. For tests.</summary>
    internal static void ClearForTests() => Blocks.Clear();

    /// <summary>
    /// Returns the next id of the sequence named by <paramref name="key"/>, reserving a new block through
    /// <paramref name="reserveBlock"/> when the current one is used up. The reservation takes the size of the block and
    /// the lowest first id it may return, and returns the first id of a block of that size.
    /// </summary>
    public static async Task<long> NextAsync(string key, Func<int, long, CancellationToken, Task<long>> reserveBlock, CancellationToken cancellationToken)
    {
        var block = Blocks.GetOrAdd(key, _ => new Block());
        await block.Gate.WaitAsync(cancellationToken);
        try
        {
            if (block.Next == 0 || block.Next > block.Last)
            {
                var lowest = block.Last + 1;
                var first = await reserveBlock(BlockSize, lowest, cancellationToken);
                if (first < lowest)
                {
                    throw new InvalidOperationException($"The reservation for '{key}' returned id {first}, below the lowest id it may return, {lowest}.");
                }

                block.Next = first;
                block.Last = first + BlockSize - 1;
            }

            return block.Next++;
        }
        finally
        {
            block.Gate.Release();
        }
    }
}
