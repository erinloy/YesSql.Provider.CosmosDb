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
    /// <paramref name="reserveBlock"/> when the current one is used up. The reservation returns the first id of a block
    /// of the requested size.
    /// </summary>
    public static async Task<long> NextAsync(string key, Func<int, CancellationToken, Task<long>> reserveBlock, CancellationToken cancellationToken)
    {
        var block = Blocks.GetOrAdd(key, _ => new Block());
        await block.Gate.WaitAsync(cancellationToken);
        try
        {
            if (block.Next == 0 || block.Next > block.Last)
            {
                var first = await reserveBlock(BlockSize, cancellationToken);
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
