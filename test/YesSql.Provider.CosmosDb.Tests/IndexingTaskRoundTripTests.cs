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
/// Orchard Core's <c>IndexingTaskManager</c> stores content-indexing tasks through a raw Dapper connection rather
/// than a YesSql session, inside a transaction:
///   flush    - delete by (Category, RecordId IN @Ids), insert the new tasks (Dapper runs the insert once per
///              task), commit.
///   retrieve - "SELECT * FROM RecordIndexingTask WHERE Id &gt; @Id AND Category = @Category ORDER BY Id
///              LIMIT @Count", paging by Id.
/// This test runs that sequence against the provider and checks that the tasks come back.
///
/// Uses a throwaway database. The endpoint defaults to http://localhost:8081/; set COSMOS_TEST_ENDPOINT to
/// use another one.
/// </summary>
public class IndexingTaskRoundTripTests
{
    // Defaults to http://localhost:8081/; set COSMOS_TEST_ENDPOINT to use another endpoint.
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") ?? "http://localhost:8081/";
    // Microsoft's published, well-known Cosmos DB emulator key (not a secret).
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
