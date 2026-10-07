using Microsoft.Azure.Cosmos;

namespace YesSql.Provider.CosmosDb.Tests;

/// <summary>
/// Connection settings for the Cosmos DB emulator used by the provider tests. The endpoint defaults to
/// http://localhost:8081/; set COSMOS_TEST_ENDPOINT to use another one.
/// </summary>
internal static class Emulator
{
    public static readonly string Endpoint =
        Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") ?? "http://localhost:8081/";

    // Microsoft's published, well-known Cosmos DB emulator key (not a secret).
    public const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public static string NewDatabaseId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    public static CosmosClientOptions ClientOptions() => new()
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        // Skip certificate validation for a loopback emulator only; a real account must be validated.
        HttpClientFactory = IsLoopback(Endpoint)
            ? () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            })
            : null,
    };

    private static bool IsLoopback(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static CosmosDbOptions Options(
        string databaseId,
        string partitionKeyPath = "/pk",
        PartitionStrategy strategy = PartitionStrategy.PerTable,
        bool createIfNotExists = true) => new()
    {
        AccountEndpoint = Endpoint,
        AccountKey = Key,
        DatabaseId = databaseId,
        PartitionKeyPath = partitionKeyPath,
        PartitionStrategy = strategy,
        PartitionScope = "tests",
        CreateIfNotExists = createIfNotExists,
        ClientOptions = ClientOptions(),
    };

    public static CosmosClient NewClient() => new(Endpoint, Key, ClientOptions());
}
