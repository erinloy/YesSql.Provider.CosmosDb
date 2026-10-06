using System;
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
    /// Partition key path of the container. Must be a single-level path such as <c>/pk</c> (the default) or
    /// <c>/tenantId</c>; the provider stores each item's partition key in that property. When the container
    /// already exists, its partition key path must match, otherwise opening a connection fails.
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
    /// the whole store. A unit of work stays in one logical partition, so its rollback is applied with
    /// transactional batches, which are atomic one batch (up to 100 operations) at a time. The store is
    /// limited to 20 GB and 10,000 RU/s.</item>
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

    /// <summary>
    /// The item property that holds the partition key, derived from <see cref="PartitionKeyPath"/>.
    /// </summary>
    internal string PartitionKeyProperty => _partitionKeyProperty ??= ParsePartitionKeyProperty(PartitionKeyPath);

    private string? _partitionKeyProperty;

    /// <summary>Throws when the options cannot be used. Called when the provider is configured.</summary>
    internal void Validate()
    {
        _ = ParsePartitionKeyProperty(PartitionKeyPath);
    }

    private static string ParsePartitionKeyProperty(string? path)
    {
        // A single path segment made of identifier characters keeps the property name safe to embed in queries.
        if (path is null || !System.Text.RegularExpressions.Regex.IsMatch(path, @"^/[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException(
                $"PartitionKeyPath '{path}' is not supported. Use a single-level path such as '/pk' or '/tenantId'.",
                nameof(PartitionKeyPath));
        }

        var property = path[1..];
        if (property.Equals("id", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PartitionKeyPath '/id' is not supported: every item would be in its own partition.",
                nameof(PartitionKeyPath));
        }

        return property;
    }
}
