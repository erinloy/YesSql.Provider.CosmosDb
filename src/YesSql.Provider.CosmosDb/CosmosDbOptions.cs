using Microsoft.Azure.Cosmos;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Connection/provisioning options for the Cosmos DB (NoSQL API) YesSql provider.
/// </summary>
public sealed class CosmosDbOptions
{
    /// <summary>Cosmos account endpoint, e.g. <c>https://localhost:8081/</c> for the emulator.</summary>
    public required string AccountEndpoint { get; init; }

    /// <summary>Cosmos account key.</summary>
    public required string AccountKey { get; init; }

    /// <summary>Database id that backs this YesSql store. Created if it does not exist.</summary>
    public required string DatabaseId { get; init; }

    /// <summary>
    /// Container id that holds all YesSql documents (single-container, type-discriminated model).
    /// Created if it does not exist. Defaults to <c>yessql</c>.
    /// </summary>
    public string ContainerId { get; init; } = "yessql";

    /// <summary>
    /// Partition key path. Kept coarse so a YesSql unit-of-work stays within one logical partition
    /// (required for atomic stored-procedure commits). Defaults to <c>/pk</c>.
    /// </summary>
    public string PartitionKeyPath { get; init; } = "/pk";

    /// <summary>When true (default), the database/container are created on first connect if absent.</summary>
    public bool CreateIfNotExists { get; init; } = true;

    /// <summary>
    /// How items are mapped to Cosmos logical partitions.
    /// <list type="bullet">
    /// <item><see cref="PartitionStrategy.PerTable"/> (default): one partition per YesSql table —
    /// horizontally scalable, but a unit of work spans partitions so there is no cross-table rollback.</item>
    /// <item><see cref="PartitionStrategy.PerStore"/>: a single partition (<see cref="PartitionScope"/>)
    /// for the whole store — a unit of work stays in one logical partition, enabling atomic rollback via
    /// a partition-scoped operation. Capped at 20 GB / 10,000 RU/s per store (ample for typical Orchard
    /// tenants, whose blobs live outside the DB).</item>
    /// </list>
    /// </summary>
    public PartitionStrategy PartitionStrategy { get; init; } = PartitionStrategy.PerTable;

    /// <summary>
    /// The single logical-partition key used when <see cref="PartitionStrategy"/> is
    /// <see cref="PartitionStrategy.PerStore"/> (e.g. the tenant/shell name). Defaults to <c>store</c>.
    /// </summary>
    public string PartitionScope { get; init; } = "store";

    /// <summary>
    /// Optional Cosmos SDK client options. Needed for the local emulator (Gateway mode + accept the
    /// self-signed certificate). Left null for normal accounts.
    /// </summary>
    public CosmosClientOptions? ClientOptions { get; init; }
}
