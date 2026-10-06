using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Xunit;
using YesSql;
using YesSql.Provider.CosmosDb;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Covers the bulk content-rewrite UPDATE that Orchard Core issues to rename serialized $type names:
///   UPDATE [Document] SET [Content] = REPLACE([Content], '&lt;from&gt;', '&lt;to&gt;') WHERE [Type] = '&lt;literal&gt;'
/// The statement has no @Id. The REPLACE arguments contain commas inside their quoted literals (as .NET type
/// names do), so argument parsing has to respect quoting.
///
/// The endpoint defaults to http://localhost:8081/; set COSMOS_TEST_ENDPOINT to use another one.
/// </summary>
public class ReplaceUpdateTests
{
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") ?? "http://localhost:8081/";
    // Microsoft's published, well-known Cosmos DB emulator key (not a secret).
    private const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    private const string ContainerId = "yessql";
    private const string Scope = "rtest";

    public class Doc
    {
        public int Id { get; set; }
        public string Payload { get; set; } = string.Empty;
    }

    private static CosmosClientOptions ClientOptions() => new()
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        HttpClientFactory = () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        }),
    };

    private static CosmosDbOptions Options(string databaseId) => new()
    {
        AccountEndpoint = Endpoint,
        AccountKey = Key,
        DatabaseId = databaseId,
        ContainerId = ContainerId,
        ClientOptions = ClientOptions(),
        PartitionStrategy = PartitionStrategy.PerStore,
        PartitionScope = Scope,
    };

    [Fact]
    public async Task Replace_update_rewrites_content_with_comma_bearing_literals_and_type_filter()
    {
        var db = "yessql_replace_" + Guid.NewGuid().ToString("N")[..8];
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options(db)));

        // Markers with commas inside them: once quoted in the SQL they become commas inside the REPLACE
        // literals, the exact shape that broke naive comma-splitting (the OrchardCore $type strings have them).
        // Kept quote-free so they survive JSON-string serialization in [Content] verbatim.
        const string oldFrag = "Old.Lucene.Query, Old.Assembly";
        const string newFrag = "New.Lucene.Query, New.Assembly";

        long id;
        await using (var s = store.CreateSession())
        {
            var d = new Doc { Payload = "BEGIN," + oldFrag + ",END" };
            await s.SaveAsync(d);
            await s.SaveChangesAsync();
            id = d.Id;
        }

        // Discover the document's stored Type so WHERE [Type] = '<literal>' matches (mirrors the migration).
        string typeName;
        using (var client = new CosmosClient(Endpoint, Key, ClientOptions()))
        {
            var resp = await client.GetContainer(db, ContainerId)
                .ReadItemAsync<JObject>($"Document:{id}", new PartitionKey(Scope));
            typeName = resp.Resource["Type"]!.ToString();
            Assert.Contains(oldFrag, resp.Resource["Content"]!.ToString());
        }

        // Execute the bulk REPLACE update through the provider (the path that used to throw).
        await using (var conn = store.Configuration.ConnectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"UPDATE [Document] SET [Content] = REPLACE([Content], '{oldFrag}', '{newFrag}') WHERE [Type] = '{typeName}'";
            var affected = await cmd.ExecuteNonQueryAsync();
            Assert.Equal(1, affected);
        }

        // The rewrite is durable + correct (commas inside the literals preserved, only the fragment swapped).
        await using (var s = store.CreateSession())
        {
            var d = await s.GetAsync<Doc>(id);
            Assert.NotNull(d);
            Assert.Equal("BEGIN," + newFrag + ",END", d!.Payload);
        }
    }

    [Fact]
    public async Task Replace_update_without_where_rewrites_all_matching_documents()
    {
        var db = "yessql_replace_" + Guid.NewGuid().ToString("N")[..8];
        var store = await StoreFactory.CreateAndInitializeAsync(new Configuration().UseCosmosDb(Options(db)));

        long id1, id2;
        await using (var s = store.CreateSession())
        {
            var a = new Doc { Payload = "x-MARK-x" };
            var b = new Doc { Payload = "y-MARK-y" };
            await s.SaveAsync(a);
            await s.SaveAsync(b);
            await s.SaveChangesAsync();
            id1 = a.Id;
            id2 = b.Id;
        }

        await using (var conn = store.Configuration.ConnectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE [Document] SET [Content] = REPLACE([Content], 'MARK', 'DONE')";
            var affected = await cmd.ExecuteNonQueryAsync();
            Assert.Equal(2, affected);
        }

        await using (var s = store.CreateSession())
        {
            Assert.Equal("x-DONE-x", (await s.GetAsync<Doc>(id1))!.Payload);
            Assert.Equal("y-DONE-y", (await s.GetAsync<Doc>(id2))!.Payload);
        }
    }
}
