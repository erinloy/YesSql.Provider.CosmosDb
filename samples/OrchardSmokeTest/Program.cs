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
                    ClientOptions = new CosmosClientOptions
                    {
                        ConnectionMode = ConnectionMode.Gateway,
                        LimitToEndpoint = true,
                        HttpClientFactory = () => new HttpClient(new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                        }),
                    },
                })
                .UseDefaultIdGenerator();

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

app.UseOrchardCore();

app.Run();
