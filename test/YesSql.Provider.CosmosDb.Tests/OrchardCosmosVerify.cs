using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Xunit;

namespace YesSql.Provider.CosmosDb.Tests;

// Verifies that the Orchard Core smoke test actually wrote its data to the Cosmos emulator
// (database "orchard_smoke"), proving Sqlite was a label only and the Cosmos IStore handled everything.
public class OrchardCosmosVerify
{
    [Fact(Skip = "Diagnostic — run manually after the Orchard smoke test (samples/OrchardSmokeTest) provisions the 'orchard_smoke' database.")]
    public async Task Dump_orchard_cosmos_contents()
    {
        using var client = new CosmosClient(
            "http://localhost:8081/",
            "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                HttpClientFactory = () => new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }),
            });

        var container = client.GetContainer("orchard_smoke", "yessql");

        var byPk = new Dictionary<string, int>();
        var total = 0;
        using var iterator = container.GetItemQueryIterator<JObject>("SELECT c.pk FROM c");
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync())
            {
                total++;
                var pk = item["pk"]?.ToString() ?? "(none)";
                byPk[pk] = byPk.TryGetValue(pk, out var n) ? n + 1 : 1;
            }
        }

        var lines = new List<string> { $"orchard_smoke / yessql total items = {total}", "" };
        foreach (var kv in byPk.OrderByDescending(k => k.Value))
        {
            lines.Add($"{kv.Value,6}  {kv.Key}");
        }

        File.WriteAllText(@"Z:\SOURCE\SCRATCH\YesSqlCosmosSpike\orchard-cosmos-contents.txt", string.Join("\n", lines));
        Assert.True(total > 0, "Expected Orchard to have written documents/index rows to Cosmos.");
    }
}
