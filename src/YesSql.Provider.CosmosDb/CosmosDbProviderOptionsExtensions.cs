using System;
using System.Data;
using YesSql.Provider.CosmosDb.Internal;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Entry-point extensions for configuring YesSql to use the Azure Cosmos DB (NoSQL API) as its backing
/// store, mirroring the shape of the first-party providers (e.g. <c>UseSqLite</c>).
/// </summary>
/// <remarks>
/// Because YesSql persists through an ADO.NET <see cref="System.Data.Common.DbConnection"/> obtained
/// from an <see cref="IConnectionFactory"/> and drives it with SQL produced by <see cref="ISqlDialect"/>,
/// this provider supplies a co-designed pair: the <see cref="CosmosDbConnection"/> ADO.NET shim plus the
/// <see cref="CosmosDbDialect"/> that emits a constrained SQL surface the shim translates into Cosmos SDK
/// operations. See SPIKE-NOTES.md.
/// </remarks>
public static class CosmosDbProviderOptionsExtensions
{
    internal const string ProviderName = "CosmosDb";

    /// <summary>
    /// Configures YesSql to store documents in an Azure Cosmos DB container.
    /// </summary>
    public static IConfiguration UseCosmosDb(this IConfiguration configuration, CosmosDbOptions options)
        => UseCosmosDb(configuration, options, IsolationLevel.Unspecified);

    /// <summary>
    /// Configures YesSql to store documents in an Azure Cosmos DB container, with an explicit isolation
    /// level (Cosmos does not use ADO.NET isolation levels; the value is carried for API parity).
    /// </summary>
    public static IConfiguration UseCosmosDb(this IConfiguration configuration, CosmosDbOptions options, IsolationLevel isolationLevel)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        configuration.SqlDialect = new CosmosDbDialect();
        configuration.CommandInterpreter = new CosmosDbCommandInterpreter();
        configuration.ConnectionFactory = new CosmosDbConnectionFactory(options);
        configuration.IsolationLevel = isolationLevel;

        return configuration;
    }
}
