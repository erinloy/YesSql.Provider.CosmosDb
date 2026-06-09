using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Azure.Cosmos;
using Xunit;
using YesSql;
using YesSql.Provider.CosmosDb;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Regression for OrchardCore CONTENT INDEXING tasks on Cosmos (the empty-Lucene-search bug). OrchardCore's
/// <c>IndexingTaskManager</c> persists indexing tasks through a RAW Dapper connection (IDbConnectionAccessor),
/// NOT the YesSql session, inside a transaction:
///   FLUSH  — delete-by-(Category, RecordId IN @Ids), then a LIST insert (Dapper runs the insert once per task),
///            then Commit.
///   RETRIEVE — a dialect-built "SELECT * FROM RecordIndexingTask WHERE Id &gt; @Id AND Category = @Category
///            ORDER BY Id LIMIT @Count" (the ContentIndexingBackgroundTask pages tasks by Id &gt; afterTaskId).
/// If any link doesn't round-trip on the Cosmos provider, no tasks are ever retrieved and the content index
/// never populates. This test replicates that exact lifecycle against the provider.
///
/// Endpoint defaults to the Aspire vnext emulator gateway (:52611 in this workspace); override with
/// COSMOS_TEST_ENDPOINT. Uses a throwaway database, so it never touches live data.
/// </summary>
public class IndexingTaskRoundTripTests
{
    // Defaults to the suite's classic-emulator convention (:8081); override with COSMOS_TEST_ENDPOINT
    // (e.g. the Aspire vnext emulator's mapped gateway port) to run it elsewhere — same as ReplaceUpdateTests.
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") ?? "http://localhost:8081/";
    private const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    private const string ContainerId = "yessql";
    private const string Scope = "Default";

    // Mirrors OrchardCore.Indexing.Models.RecordIndexingTask (Id identity, RecordId, Category, CreatedUtc, Type).
    public sealed class RecordIndexingTask
    {
        public long Id { get; set; }
        public string RecordId { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public int Type { get; set; }
    }

    private static CosmosDbOptions Options(string databaseId) => new()
    {
        AccountEndpoint = Endpoint,
        AccountKey = Key,
        DatabaseId = databaseId,
        ContainerId = ContainerId,
        ClientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        },
        PartitionStrategy = PartitionStrategy.PerStore,
        PartitionScope = Scope,
    };

    [Fact]
    public async Task Indexing_tasks_round_trip_through_the_dapper_connection()
    {
        var db = "yessql_idxtask_" + Guid.NewGuid().ToString("N")[..8];
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options(db)));

        const string category = "Content";
        var tasks = new List<RecordIndexingTask>
        {
            new() { RecordId = "achomepageaaaaaaaaaaaaaaaa", Category = category, CreatedUtc = DateTime.UtcNow, Type = 0 },
            new() { RecordId = "acaboutpageaaaaaaaaaaaaaaa", Category = category, CreatedUtc = DateTime.UtcNow, Type = 0 },
            new() { RecordId = "achowitworksaaaaaaaaaaaaaa", Category = category, CreatedUtc = DateTime.UtcNow, Type = 0 },
        };

        var dialect = store.Configuration.SqlDialect;
        var schema = store.Configuration.Schema;
        var table = store.Configuration.TablePrefix + nameof(RecordIndexingTask);

        // --- FLUSH (replicates OrchardCore IndexingTaskManager.FlushAsync) ---
        await using (var connection = store.Configuration.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync(store.Configuration.IsolationLevel);

            var deleteCmd = $"delete from {dialect.QuoteForTableName(table, schema)} where " +
                $"{dialect.QuoteForColumnName("Category")} = @Category and " +
                $"{dialect.QuoteForColumnName("RecordId")} {dialect.InOperator("@Ids")};";
            await transaction.Connection!.ExecuteAsync(deleteCmd,
                new { Category = category, Ids = tasks.Select(t => t.RecordId).ToArray() }, transaction);

            var insertCmd = $"insert into {dialect.QuoteForTableName(table, schema)} (" +
                $"{dialect.QuoteForColumnName("CreatedUtc")}, {dialect.QuoteForColumnName("RecordId")}, " +
                $"{dialect.QuoteForColumnName("Category")}, {dialect.QuoteForColumnName("Type")}) " +
                "values (@CreatedUtc, @RecordId, @Category, @Type);";
            await transaction.Connection!.ExecuteAsync(insertCmd, tasks, transaction);

            await transaction.CommitAsync();
        }

        // --- RETRIEVE (replicates OrchardCore IndexingTaskManager.GetIndexingTasksAsync) ---
        List<RecordIndexingTask> retrieved;
        await using (var connection = store.Configuration.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();

            var sqlBuilder = dialect.CreateBuilder(store.Configuration.TablePrefix);
            sqlBuilder.Select();
            sqlBuilder.Table(nameof(RecordIndexingTask), alias: null, store.Configuration.Schema);
            sqlBuilder.Selector("*");
            sqlBuilder.Take("100");
            sqlBuilder.WhereAnd($"{dialect.QuoteForColumnName("Id")} > @Id");
            sqlBuilder.WhereAnd($"{dialect.QuoteForColumnName("Category")} = @Category");
            sqlBuilder.OrderBy(dialect.QuoteForColumnName("Id"));

            retrieved = (await connection.QueryAsync<RecordIndexingTask>(sqlBuilder.ToSqlString(),
                new { Id = 0L, Category = category })).ToList();
        }

        // The background task reads zero tasks → the content index never populates if any of these fail.
        Assert.Equal(3, retrieved.Count);
        Assert.All(retrieved, t => Assert.Equal(category, t.Category));
        Assert.All(retrieved, t => Assert.True(t.Id > 0, "each task must receive a positive identity Id"));
        Assert.Contains(retrieved, t => t.RecordId == "achomepageaaaaaaaaaaaaaaaa");

        // The pager fetches "Id > afterTaskId" — confirm only later tasks come back on a second page.
        var afterFirst = retrieved.OrderBy(t => t.Id).First().Id;
        await using (var connection = store.Configuration.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            var sqlBuilder = dialect.CreateBuilder(store.Configuration.TablePrefix);
            sqlBuilder.Select();
            sqlBuilder.Table(nameof(RecordIndexingTask), alias: null, store.Configuration.Schema);
            sqlBuilder.Selector("*");
            sqlBuilder.WhereAnd($"{dialect.QuoteForColumnName("Id")} > @Id");
            sqlBuilder.WhereAnd($"{dialect.QuoteForColumnName("Category")} = @Category");
            sqlBuilder.OrderBy(dialect.QuoteForColumnName("Id"));
            var page2 = (await connection.QueryAsync<RecordIndexingTask>(sqlBuilder.ToSqlString(),
                new { Id = afterFirst, Category = category })).ToList();
            Assert.Equal(2, page2.Count);
        }
    }
}
