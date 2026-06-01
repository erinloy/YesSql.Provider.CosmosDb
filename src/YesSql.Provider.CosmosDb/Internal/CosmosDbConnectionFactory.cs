using System;
using System.Data.Common;

namespace YesSql.Provider.CosmosDb.Internal;

/// <summary>
/// <see cref="IConnectionFactory"/> that hands YesSql a Cosmos-backed ADO.NET connection. Unlike the
/// generic <c>DbConnectionFactory&lt;T&gt;</c>, Cosmos needs structured options (endpoint, key, database,
/// container, partition key) rather than a single connection string, so this factory carries them.
/// </summary>
public sealed class CosmosDbConnectionFactory : IConnectionFactory
{
    private readonly CosmosDbOptions _options;

    public CosmosDbConnectionFactory(CosmosDbOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Type DbConnectionType => typeof(CosmosDbConnection);

    public DbConnection CreateConnection() => new CosmosDbConnection(_options);
}
