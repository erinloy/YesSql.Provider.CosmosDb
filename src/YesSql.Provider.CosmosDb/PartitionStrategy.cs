namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Determines how the provider maps YesSql items to Cosmos DB logical partitions.
/// </summary>
public enum PartitionStrategy
{
    /// <summary>
    /// One logical partition per YesSql table (the table name is the partition key). Horizontally
    /// scalable, but a unit of work spans partitions, so there is no cross-table atomic rollback.
    /// </summary>
    PerTable = 0,

    /// <summary>
    /// A single logical partition for the whole store (CosmosDbOptions.PartitionScope). A unit of work
    /// stays within one logical partition, enabling atomic rollback. Bounded by Cosmos's 20 GB / 10,000
    /// RU/s per-logical-partition limits.
    /// </summary>
    PerStore = 1,
}
