using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using YesSql.Provider.CosmosDb.Internal.Sql;

namespace YesSql.Provider.CosmosDb.Internal;

// Ids for index rows, which Cosmos has no auto-increment for.
internal sealed partial class CosmosDbCommand
{
    // Monotonic, never-reused id allocator for index rows (auto-increment has no Cosmos equivalent, and
    // MAX+1 reuses ids after deletes, which breaks YesSql's append-only index expectations). A counter
    // doc per table lives in an isolated "__seq" partition so it never appears in index/count queries. The counter
    // holds the last id reserved, and ids are reserved in blocks (see SequenceBlocks), so an insert normally costs no
    // round trip for its id and concurrent inserts do not contend on the counter.
    private Task<long> NextSequenceAsync(string table, CancellationToken cancellationToken)
    {
        var options = _connection.Options;
        var key = $"{options.AccountEndpoint}|{options.DatabaseId}|{options.ContainerId}|{table}";
        return SequenceBlocks.NextAsync(key, (size, lowest, token) => ReserveSequenceBlockAsync(table, size, lowest, token), cancellationToken);
    }

    // Reserves `size` ids from the table's counter with a conditional write and returns the first one. The first id is
    // at least `lowest`: this process has issued the ids below it, and they may still be on their way to Cosmos, so the
    // counter, or the largest stored id when there is no counter, can be behind them.
    private async Task<long> ReserveSequenceBlockAsync(string table, int size, long lowest, CancellationToken cancellationToken)
    {
        var seqPk = new PartitionKey("__seq");

        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (attempt > 0)
            {
                // Concurrent allocators for the same table collide on the counter's ETag; spread the retries.
                await Task.Delay(Random.Shared.Next(2, 20 * (attempt + 1)), cancellationToken);
            }

            try
            {
                var current = await CosmosContainer.ReadItemAsync<JObject>(table, seqPk, cancellationToken: cancellationToken);
                var last = Math.Max(current.Resource["next"]?.ToObject<long>() ?? 0, lowest - 1);
                current.Resource["next"] = last + size;
                await CosmosContainer.ReplaceItemAsync(current.Resource, table, seqPk,
                    new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
                return last + 1;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                var max = Math.Max(await MaxIdAsync(table, cancellationToken) ?? 0, lowest - 1);
                try
                {
                    await CosmosContainer.CreateItemAsync(new JObject { ["id"] = table, [PartitionKeyProperty] = "__seq", ["next"] = max + size }, seqPk, cancellationToken: cancellationToken);
                    return max + 1;
                }
                catch (CosmosException dup) when (dup.StatusCode == HttpStatusCode.Conflict)
                {
                    // created concurrently, retry the read/increment path
                }
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // lost the ETag race, retry
            }
        }

        throw new InvalidOperationException($"Could not reserve a block of ids for '{table}'.");
    }

    private async Task<long?> MaxIdAsync(string table, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT VALUE MAX(c.Id) FROM c WHERE " + Scoped(table)).WithParameter("@pk", PkValue(table));
        using var iterator = CosmosContainer.GetItemQueryIterator<long?>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = PartitionKeyFor(table) });

        while (iterator.HasMoreResults)
        {
            foreach (var v in await iterator.ReadNextAsync(cancellationToken))
            {
                return v;
            }
        }

        return null;
    }
}
