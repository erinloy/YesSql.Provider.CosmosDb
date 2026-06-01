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
}
