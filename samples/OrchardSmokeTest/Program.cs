using System.Net.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using OrchardCore.Data;
using OrchardCore.Environment.Shell;
using OrchardCore.Json;
using YesSql;
using YesSql.Indexes;
using YesSql.Provider.CosmosDb;
using YesSql.Serialization;

var builder = WebApplication.CreateBuilder(args);

var cosmos = builder.Configuration.GetSection("Cosmos");

builder.Services
    .AddOrchardCms()
    .AddSetupFeatures("OrchardCore.AutoSetup")
    // Override the per-tenant IStore so Orchard runs on Cosmos DB instead of the configured relational
    // provider. Registered after AddOrchardCms so it wins; mirrors Orchard's own GetStoreConfiguration.
    .ConfigureServices(services =>
    {
        services.AddSingleton<IStore>(sp =>
        {
            var shellSettings = sp.GetRequiredService<ShellSettings>();
            if (shellSettings.IsUninitialized() || shellSettings["DatabaseProvider"] is null)
            {
                return null;
            }

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var serializerOptions = sp.GetRequiredService<IOptions<DocumentJsonSerializerOptions>>();

            var configuration = new YesSql.Configuration
            {
                IdentityColumnSize = IdentityColumnSize.Int64,
                Logger = loggerFactory.CreateLogger("YesSql"),
                ContentSerializer = new DefaultContentJsonSerializer(serializerOptions.Value.SerializerOptions),
            };

            configuration
                .UseCosmosDb(new CosmosDbOptions
                {
                    AccountEndpoint = cosmos["Endpoint"],
                    AccountKey = cosmos["Key"],
                    DatabaseId = cosmos["Database"],
                    // PerTable is the default. PerStore keeps a tenant's data in one partition, named after the tenant.
                    PartitionStrategy = Enum.Parse<PartitionStrategy>(cosmos["PartitionStrategy"] ?? nameof(PartitionStrategy.PerTable)),
                    PartitionScope = shellSettings.Name,
                    ClientOptions = new CosmosClientOptions
                    {
                        ConnectionMode = ConnectionMode.Gateway,
                        LimitToEndpoint = true,
                        // The emulator can present a self-signed certificate. Skip certificate validation for a
                        // loopback endpoint only; a real Cosmos DB account must always be validated.
                        HttpClientFactory = IsLoopback(cosmos["Endpoint"])
                            ? () => new HttpClient(new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                            })
                            : null,
                    },
                })
                // Orchard uses the block generator for every database that more than one process can reach (SQL Server, MySQL and
                // PostgreSQL), and the default one only for SQLite. A Cosmos DB account is reached by every node, so it takes the block one.
                .UseBlockIdGenerator();

            var tablePrefix = shellSettings["TablePrefix"];
            if (!string.IsNullOrWhiteSpace(tablePrefix))
            {
                configuration.SetTablePrefix(tablePrefix.Trim() + "_");
            }

            var store = StoreFactory.Create(configuration);
            store.RegisterIndexes(sp.GetServices<IIndexProvider>());
            return store;
        });
    });

var app = builder.Build();

// With SmokeTest:ContentChecks set, GET /_smoke/content runs the content operations of ContentChecks against the tenant
// and answers with the result of every check. It is not mapped otherwise.
if (app.Configuration.GetValue<bool>("SmokeTest:ContentChecks"))
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.Equals("/_smoke/content", StringComparison.Ordinal))
        {
            await next();
            return;
        }

        var checks = await ContentChecks.RunAsync(context.RequestServices.GetRequiredService<IShellHost>());
        context.Response.StatusCode = checks.All(check => check.Ok) ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(checks);
    });
}

app.UseOrchardCore();

app.Run();

static bool IsLoopback(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
