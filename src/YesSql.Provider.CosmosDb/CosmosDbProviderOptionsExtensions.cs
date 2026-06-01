using YesSql;

namespace YesSql.Provider.CosmosDb;

/// <summary>
/// Entry-point extensions for configuring YesSql to use the Azure Cosmos DB (NoSQL API)
/// as its backing store, mirroring the shape of the first-party providers (e.g. <c>UseSqLite</c>).
/// </summary>
/// <remarks>
/// Because YesSql persists through an ADO.NET <see cref="System.Data.Common.DbConnection"/> obtained
/// from an <see cref="IConnectionFactory"/> and drives it with SQL produced by <see cref="ISqlDialect"/>,
/// this provider supplies a co-designed pair: a Cosmos-backed ADO.NET shim plus a dialect that emits a
/// constrained SQL surface the shim translates into Cosmos SDK operations. Implementation lands across
/// the spike milestones — see SPIKE-NOTES.md.
/// </remarks>
public static class CosmosDbProviderOptionsExtensions
{
    internal const string ProviderName = "CosmosDb";

    // UseCosmosDb(this IConfiguration, ...) — implemented in Milestone 2 once the
    // IConnectionFactory / ISqlDialect / ICommandInterpreter triad is in place.
}
