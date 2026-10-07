namespace YesSql.Provider.CosmosDb.Tests.Sql;

/// <summary>
/// Tests that compare timings run alone. xUnit runs test classes in parallel, and a neighbouring test that is
/// using every core can make a run take several times longer than it did a moment before.
/// </summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection
{
}
