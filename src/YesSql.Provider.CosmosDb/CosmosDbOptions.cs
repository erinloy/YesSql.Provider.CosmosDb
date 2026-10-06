using Microsoft.Azure.Cosmos;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Connection/provisioning options for the Cosmos DB (NoSQL API) YesSql provider.
/// </summary>
public sealed class CosmosDbOptions
{
    /// <summary>Cosmos account endpoint, e.g. <c>https://my-account.documents.azure.com:443/</c>.</summary>
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
    /// Partition key path of the container. The provider stores each item's partition key in its
    /// <c>pk</c> property, so this must remain <c>/pk</c> (the default).
    /// </summary>
    public string PartitionKeyPath { get; init; } = "/pk";

    /// <summary>When true (default), the database/container are created on first connect if absent.</summary>
    public bool CreateIfNotExists { get; init; } = true;

    /// <summary>
    /// How items are mapped to Cosmos logical partitions.
    /// <list type="bullet">
    /// <item><see cref="PartitionStrategy.PerTable"/> (default): one partition per YesSql table. This
    /// scales out, but a unit of work spans partitions, so rollback is best effort.</item>
    /// <item><see cref="PartitionStrategy.PerStore"/>: one partition (<see cref="PartitionScope"/>) for
    /// the whole store. A unit of work stays in one logical partition, so its rollback is atomic. The
    /// store is limited to 20 GB and 10,000 RU/s.</item>
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
